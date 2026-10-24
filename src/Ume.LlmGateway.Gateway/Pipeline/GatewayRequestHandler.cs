using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// The data-plane pipeline: authenticate → parse → resolve model → rate limit → PII guard → route →
/// reserve budget → call provider(s) with fallback → account. Request and response content is only held
/// in memory for the duration of the call and is never logged or persisted. The stages shared with live sessions
/// (<see cref="HandleRealtimeAsync"/>) are in <c>GatewayRequestHandler.Stages.cs</c>, accounting in
/// <c>GatewayRequestHandler.Accounting.cs</c>. Once a budget is reserved, every path accounts exactly once.
/// </summary>
public sealed partial class GatewayRequestHandler(
    KeyAuthenticator keys,
    AuthFailureThrottle authThrottle,
    GatewayCatalog catalog,
    IRouteResolver router,
    IRateLimiter rateLimiter,
    BudgetService budgets,
    ICircuitBreakerStore circuits,
    IOptions<CircuitBreakerOptions> circuitOptions,
    DeploymentLoad load,
    ContentGuardRules guardRules,
    IEnumerable<IProviderAdapter> adapters,
    IRealtimeConnector realtime,
    IRealtimeSessionRegistry sessions,
    IHostApplicationLifetime lifetime,
    CredentialProtector credentials,
    UsageWriter usageWriter,
    AuthFailureRecorder authFailures,
    GatewayMetrics metrics,
    IOptionsMonitor<GatewayOptions> options,
    TimeProvider time,
    ILoggerFactory loggers,
    ILogger<GatewayRequestHandler> logger)
{
    // A repeated property is refused (400): the parsed tree would otherwise fail later with a 500, and a provider reading the
    // last value could see a different model or text than the one checked here.
    internal static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64, AllowDuplicateProperties = false };

    /// <summary>Bodies this large are walked for images even when the key allows all files (see the token estimate).</summary>
    private const long AttachmentInspectBytes = 32 * 1024;
    private readonly ProviderAdapterLookup _adapters = new(adapters);
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public async Task HandleAsync(HttpContext http, GatewayEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var info = GatewayEndpoints.Info(endpoint);
        var state = new RequestState(RequestIdMiddleware.Get(http), endpoint, time.GetTimestamp());
        var now = time.GetUtcNow();

        // 1. Authenticate the virtual key.
        var key = await AuthenticateAsync(http, endpoint, now, ct);
        if (key is null)
        {
            return;
        }

        state.Key = key;

        // 2. Parse body (strict JSON object with a model name; the audio endpoints take a multipart upload instead).
        var (body, bodyLength, audio, images) = await ReadBodyAsync(http, state, info, ct);
        if (body is null)
        {
            return;
        }

        // The upload stays in memory until the last attempt is done; disposing returns the pooled buffer.
        using var audioLifetime = audio;
        using var imagesLifetime = images;

        if (body["model"] is not JsonValue modelValue || !modelValue.TryGetValue<string>(out var model) || string.IsNullOrWhiteSpace(model))
        {
            await RejectAsync(http, state, GatewayError.MissingModel, RequestOutcome.Rejected);
            return;
        }

        state.RequestedModel = ModelNames.Truncate(model);

        if (info.IsImage && RequestRewriter.IsStreaming(body))
        {
            await RejectAsync(http, state, GatewayError.ImageStreamingNotSupported, RequestOutcome.Rejected);
            return;
        }

        // 2b. Attachment policy (per key): files are base64 the PII guard cannot read, so sensitive keys may refuse them.
        //     Large bodies are inspected regardless, so inline images do not count as text in the token estimate.
        var attachments = audio is not null ? new AttachmentScan(AttachmentKinds.Audio, 0, 0)
            : images is not null ? new AttachmentScan(AttachmentKinds.Image, images.ImageCount, 0)
            : key.AttachmentPolicy != AttachmentPolicy.Allowed || bodyLength >= AttachmentInspectBytes ? AttachmentScanner.Inspect(body)
            : default;
        if (AttachmentScanner.Disallowed(key.AttachmentPolicy, attachments.Kinds) is var refused and not AttachmentKinds.None)
        {
            await RejectAsync(http, state, GatewayError.AttachmentNotAllowed(refused, key.AttachmentPolicy == AttachmentPolicy.ImagesOnly), RequestOutcome.Rejected);
            return;
        }

        // 2c. Provider tools and storage (per key): fields that send the conversation beyond the provider's region or keep it there.
        if (ProviderFeaturePolicy.Apply(body, endpoint, key.AllowProviderTools, key.AllowProviderStorage) is { } feature)
        {
            await RejectAsync(http, state, GatewayError.ProviderFeatureNotAllowed(feature), RequestOutcome.Rejected);
            return;
        }

        // 3. Resolve model alias and check the key's allow-list and endpoint compatibility.
        var snapshot = await catalog.GetAsync(ct);
        var resolution = router.ResolveModel(snapshot, key, endpoint, model);
        if (resolution.Rejection is { } modelRejection)
        {
            await RejectAsync(http, state, modelRejection, RequestOutcome.Rejected);
            return;
        }

        var requested = resolution.Model;
        // Issued now so it is pipelined with the rate-limit call below instead of costing its own round trip.
        var circuitPrefetch = requested is null ? null : router.PrefetchCircuits(requested, ct);

        // 4. Rate limits (requests and tokens per minute).
        // Audio: from the file's playing time (both the per-minute and the per-token way of pricing speech models).
        var audioEstimate = audio is null ? default : CostCalculator.EstimateTranscription(audio.EstimatedSeconds);
        // A free endpoint (token counting) counts no tokens, not even estimated ones (rate limit, a cancelled request).
        var estimatedInputTokens = info.IsFree ? 0 : audio is null ? CostCalculator.EstimateInputTokens(bodyLength, attachments) : audioEstimate.InputTokens;
        state.EstimatedInputTokens = estimatedInputTokens;
        state.EstimatedAudioSeconds = audioEstimate.AudioSeconds;
        if (!await AcquireRateLimitAsync(http, state, key, ct))
        {
            return;
        }

        // 5. PII guard (optional per key).
        DataResidency? restrictTo = null;
        bool? piiDetected = null;
        if (key.PiiPolicy != PiiPolicy.Off)
        {
            var scan = PiiJsonScanner.Scan(body, redact: key.PiiPolicy == PiiPolicy.Redact);
            var decision = PiiJsonScanner.Decide(key.PiiPolicy, scan);
            state.PiiCategories = scan.Summary;
            piiDetected = scan.HasPii;
            if (scan.HasPii)
            {
                state.PiiAction = key.PiiPolicy;
                http.Response.Headers[GatewayHeaders.Pii] = decision switch
                {
                    PiiDecision.ForwardRedacted => "redacted",
                    PiiDecision.Block => "blocked",
                    PiiDecision.ForwardOnPremOnly => "rerouted-onprem",
                    _ => "detected",
                };
            }

            if (decision == PiiDecision.Block)
            {
                await RejectAsync(http, state, GatewayError.PiiBlocked(scan.Summary), RequestOutcome.PiiBlocked);
                return;
            }

            if (decision == PiiDecision.ForwardOnPremOnly)
            {
                restrictTo = DataResidency.OnPrem;
            }
        }

        // 5b. Content guard (optional per key): after the PII guard, so it reads the redacted text; before routing, so a
        //     refused request costs nothing more.
        if (key.ContentGuardPolicy != ContentGuardPolicy.Off && !await GuardRequestAsync(http, state, snapshot, key, body, ct))
        {
            return;
        }

        // 6. Routing rules (optional): conditions on the request may rewrite where it goes. Everything below
        //    (provider allow-list, residency, PII restriction, health) still applies to the models the rules chose.
        if (piiDetected is null && snapshot.Rules.Count > 0 && snapshot.Rules.References("pii_detected"))
        {
            piiDetected = PiiJsonScanner.Scan(body, redact: false).HasPii; // signal only: no policy action is taken
        }

        var routing = await ApplyRoutingAsync(http, state, snapshot, key, model, body, estimatedInputTokens, piiDetected, ct);

        // 7. Candidate providers in attempt order.
        var stream = info.SupportsStreaming && RequestRewriter.IsStreaming(body);
        state.Streamed = stream;
        // Audio and images are counted by length or picture, not in tokens a context window limits.
        var contextTokens = audio is null && !info.IsImage ? estimatedInputTokens : 0;
        var route = await SelectRouteAsync(http, state, snapshot, key, model, requested, routing, restrictTo, stream, circuitPrefetch, ct, contextTokens,
            SessionAffinity(http, body, key));
        if (route is not { } selected)
        {
            return;
        }

        // 7b. The team's and key's request policy (output-token cap, system prompt, parameter defaults and overrides).
        //     After the PII guard and the rules, which see what the client sent; before the budget estimate it changes.
        if (audio is null && images is null && RequestPolicy.Merge(key.Team?.RequestPolicy, key.RequestPolicy) is { } policy)
        {
            var promptCharacters = RequestPolicies.Apply(body, endpoint, policy);
            if (promptCharacters > 0 && !info.IsFree)
            {
                estimatedInputTokens += CostCalculator.EstimateTokens(promptCharacters);
                state.EstimatedInputTokens = estimatedInputTokens;
            }
        }

        // 8. Budget reservation across key, team and förvaltning.
        long maxOutput = info.HasOutputTokens ? RequestRewriter.RequestedMaxOutputTokens(body) ?? options.CurrentValue.DefaultOutputTokenEstimate : 0;
        var expectedUsage = info.IsFree ? default
            : audio is not null ? audioEstimate
            : info.IsImage ? CostCalculator.EstimateImages(estimatedInputTokens, RequestRewriter.RequestedImageCount(body))
            : new TokenUsage(estimatedInputTokens, 0, maxOutput);
        var estimate = selected.Candidates.Max(t => CostCalculator.Calculate(expectedUsage, t.ModelDeployment!.PriceAt(now)));
        var applicable = BudgetService.ApplicableBudgets(snapshot, key);
        // Token quotas limited to a residency need the candidates' residencies; without one, nothing extra is computed.
        HashSet<DataResidency>? residencies = BudgetService.HasTokenQuota(applicable)
            ? [.. selected.Candidates.Select(t => t.ModelDeployment!.ProviderAccount!.Residency)]
            : null;
        if (await TryReserveAsync(http, state, snapshot, applicable, estimate, ct, expectedUsage.InputTokens + expectedUsage.OutputTokens, residencies) is null)
        {
            return;
        }

        // 9. Attempt candidates in order. From here on the reservation must be reconciled whatever happens.
        try
        {
            await AttemptAsync(http, state, snapshot, body, audio, images, selected, ct);
        }
        catch (Exception ex) when (!state.Accounted)
        {
            if (!await AccountUnexpectedAsync(http, state, snapshot, ex))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Calls the candidates in order, falling back while nothing has been sent to the client, and accounts the outcome.
    /// The last candidate is retried after a passing failure (<see cref="RetryOptions"/>). <c>x-ume-fallbacks</c> and
    /// the usage record count every extra attempt.
    /// </summary>
    private async Task AttemptAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, JsonObject body, AudioUpload? audio,
        ImageUploads? images, SelectedRoute route, CancellationToken ct)
    {
        var candidates = route.Candidates;
        var now = time.GetUtcNow();
        var clientWantsStreamUsage = (body["stream_options"] as JsonObject)?["include_usage"] is JsonValue iu && iu.TryGetValue<bool>(out var wants) && wants;
        ProviderFailure? lastFailure = null;
        var retry = options.CurrentValue.Retry;
        var retriesLeft = retry.MaxRetries;
        var attempt = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var deployment = candidates[i].ModelDeployment!;
            var provider = deployment.ProviderAccount!;
            state.Fallbacks = attempt++;
            state.Deployment = deployment;

            if (!TryGetCredential(snapshot, provider, out var credential))
            {
                lastFailure = ProviderFailure.CredentialUnavailable;
                continue;
            }

            // The rewrite mutates the body; only keep a pristine copy when a fallback attempt may still need it. A retry
            // of the last candidate reuses its body: the rewrite for the same deployment gives the same result.
            var attemptBody = i == candidates.Count - 1 ? body : (JsonObject)body.DeepClone();
            RequestRewriter.Apply(attemptBody, state.Endpoint, deployment.UpstreamModel, deployment.ParameterProfile, state.Streamed);

            ProviderResult result;
            // In flight until the attempt ends (a relayed stream included); feeds least-loaded balancing.
            using var lease = load.Start(deployment.Id);
            var sent = time.GetTimestamp();
            state.AttemptTimestamp = sent;
            try
            {
                result = await _adapters.Resolve(provider.Type)
                    .SendAsync(new ProviderCall(provider, deployment, state.Endpoint, attemptBody, state.Streamed, credential, clientWantsStreamUsage, audio, images,
                        options.CurrentValue.PromptCaching), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                state.AddUpstreamTime(time.GetTimestamp() - sent);
                await AccountAsync(state, deployment, new TokenUsage(state.EstimatedInputTokens, 0, 0, state.EstimatedAudioSeconds), 499, RequestOutcome.ClientCancelled, null, snapshot);
                return;
            }

            // Failed attempts count too: time spent waiting on providers is not gateway overhead.
            state.AddUpstreamTime(time.GetTimestamp() - sent);
            lease.Complete(result is not ProviderFailure { Kind: not FailureKind.ClientError }, time.GetElapsedTime(sent));
            switch (result)
            {
                case ProviderFailure failure when failure.CanFallBack:
                    await RecordFailureAsync(state, deployment, failure);
                    lastFailure = failure;
                    if (i == candidates.Count - 1 && failure.IsRetryable && retriesLeft > 0)
                    {
                        var delay = RetryDelay(retry, retry.MaxRetries - retriesLeft);
                        retriesLeft--;
                        LogRetrying(logger, state.RequestId, provider.Name, (long)delay.TotalMilliseconds);
                        await Task.Delay(delay, time, ct);
                        i--; // the same candidate again
                    }

                    continue;

                case ProviderFailure failure:
                    // Client error (e.g. invalid parameter): retrying elsewhere would fail the same way.
                    SetRoutingHeaders(http, deployment, state.Fallbacks);
                    // Only the provider's message, type, code and param are passed on (ProviderErrors).
                    http.Response.StatusCode = failure.StatusCode;
                    http.Response.ContentType = "application/json; charset=utf-8";
                    await http.Response.WriteAsync(ProviderErrors.ClientBody(state.Endpoint, failure.Body, state.RequestId), ct);

                    await AccountAsync(state, deployment, default, failure.StatusCode, RequestOutcome.ProviderError, GatewayErrorCodes.ProviderRejected, snapshot);
                    return;

                case ProviderJsonResult json:
                    using (json)
                    using (usageWriter.TrackPending())
                    {
                        await RecordSuccessAsync(state, provider);
                        if (await GuardAnswerAsync(http, state, snapshot, state.Key!, json.Body, ct) is { } withheld)
                        {
                            // The provider answered (and is paid); the client gets the refusal instead of the answer.
                            SetRoutingHeaders(http, deployment, state.Fallbacks);
                            await GatewayErrors.WriteAsync(http, state.Endpoint, withheld, options.CurrentValue.DocsUrl);
                            await AccountAsync(state, deployment, json.Usage, withheld.StatusCode, RequestOutcome.Rejected, withheld.Code, snapshot);
                            return;
                        }

                        var cost = CostCalculator.Calculate(json.Usage, deployment.PriceAt(now));
                        SetRoutingHeaders(http, deployment, state.Fallbacks);
                        http.Response.Headers[GatewayHeaders.CostUsd] = cost.ToString("0.00000000", CultureInfo.InvariantCulture);
                        if (state.Reservation?.RemainingUsd is { } rem)
                        {
                            SetBudgetRemainingHeaders(http, Math.Max(0, rem - cost));
                        }

                        // The answer goes out first (complete, thanks to Content-Length); accounting then runs off the
                        // client's critical path. TrackPending keeps UsageWriter.FlushAsync waiting for it.
                        http.Response.StatusCode = json.StatusCode;
                        http.Response.ContentType = json.ContentType ?? "application/json";
                        http.Response.ContentLength = json.Body.Length;
                        await http.Response.Body.WriteAsync(json.Body, CancellationToken.None);
                        await AccountAsync(state, deployment, json.Usage, json.StatusCode, RequestOutcome.Success, null, snapshot);
                    }

                    return;

                case ProviderStreamResult streamResult:
                    using (streamResult)
                    {
                        await RecordSuccessAsync(state, provider);
                        await StreamAsync(http, state, deployment, state.Fallbacks, streamResult, snapshot, ct);
                    }

                    return;
            }
        }

        // 10. Every candidate failed.
        await RejectAllFailedAsync(http, state, attempt, route.ModelName, lastFailure);
    }

    private async Task StreamAsync(HttpContext http, RequestState state, ModelDeployment deployment, int fallbacks, ProviderStreamResult result, CatalogSnapshot snapshot, CancellationToken ct)
    {
        SetRoutingHeaders(http, deployment, fallbacks);
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var status = 200;
        var outcome = RequestOutcome.Success;
        string? errorCode = null;
        // Reading the stream is provider time: the gateway only relays events as they arrive.
        var streaming = time.GetTimestamp();
        var gaps = new StreamGaps(time);
        long written = 0;
        try
        {
            await http.Response.StartAsync(ct);
            var writer = http.Response.BodyWriter;
            await foreach (var evt in result.Events.WithCancellation(ct))
            {
                var arrived = time.GetTimestamp();
                if (state.FirstEventTimestamp != 0)
                {
                    gaps.Upstream(arrived - written);
                }
                else
                {
                    state.ProviderFirstEventTimestamp = arrived;
                }

                // Encoded straight into the response pipe; one flush per event keeps tokens flowing immediately.
                evt.WriteTo(writer);
                await writer.FlushAsync(ct);
                written = time.GetTimestamp();
                gaps.Write(written - arrived);
                if (state.FirstEventTimestamp == 0)
                {
                    state.FirstEventTimestamp = written;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = 499;
            outcome = RequestOutcome.ClientCancelled;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            // Upstream broke mid-stream (or sent what a translator cannot read); headers are already sent, so no fallback is possible.
            LogStreamBroken(logger, state.RequestId, deployment.ProviderAccount!.Name, ex.GetType().Name);
            status = 502;
            outcome = RequestOutcome.ProviderError;
            errorCode = "stream_interrupted";
        }

        state.AddUpstreamTime(time.GetTimestamp() - streaming);
        RecordStreamGaps(state, deployment, gaps);
        await AccountAsync(state, deployment, result.Usage.ToTokenUsage(state.EstimatedInputTokens, state.EstimatedAudioSeconds), status, outcome, errorCode, snapshot);
    }

    /// <summary>
    /// The request body: a JSON object, or the form fields and files of an audio or image upload. A null body means the
    /// request was refused (the answer has been written and accounted).
    /// </summary>
    private async Task<(JsonObject? Body, long Length, AudioUpload? Audio, ImageUploads? Images)> ReadBodyAsync(HttpContext http, RequestState state, GatewayEndpointInfo info, CancellationToken ct)
    {
        if (info.IsAudio)
        {
            var form = await AudioFormReader.ReadAsync(http.Request, AllowBodyOf(http, options.CurrentValue.MaxAudioRequestBodyBytes), ct);
            if (form.Error is { } audioError)
            {
                await RejectAsync(http, state, audioError, RequestOutcome.Rejected);
                return default;
            }

            return (form.Fields ?? new JsonObject(), http.Request.ContentLength ?? form.Audio!.Data.Length, form.Audio, null);
        }

        if (info.IsImageUpload)
        {
            var form = await ImageFormReader.ReadAsync(http.Request, AllowBodyOf(http, options.CurrentValue.MaxImageRequestBodyBytes), ct);
            if (form.Error is { } imageError)
            {
                await RejectAsync(http, state, imageError, RequestOutcome.Rejected);
                return default;
            }

            // The size that counts as text is the form fields', not the pictures' (they are counted per picture).
            var fields = form.Fields ?? new JsonObject();
            return (fields, fields.ToJsonString().Length, null, form.Images);
        }

        if (!http.Request.HasJsonContentType())
        {
            await RejectAsync(http, state, GatewayError.JsonRequired, RequestOutcome.Rejected);
            return default;
        }

        var (body, length, status) = await ReadJsonAsync(http.Request, ct);
        if (status != 0)
        {
            await RejectAsync(http, state, status == 413 ? GatewayError.RequestTooLarge : GatewayError.InvalidJson, RequestOutcome.Rejected);
            return default;
        }

        if (body is null)
        {
            // Valid JSON, but not an object.
            await RejectAsync(http, state, GatewayError.MissingModel, RequestOutcome.Rejected);
            return default;
        }

        return (body, length, null, null);
    }

    /// <summary>Raises the server's request body limit to an upload endpoint's own, and returns it.</summary>
    private static long AllowBodyOf(HttpContext http, long maxBytes)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = maxBytes;
        }

        return maxBytes;
    }

    /// <summary>Reads the body into one pooled buffer and parses it. A non-zero status (413, 400) means it is refused.</summary>
    private async Task<(JsonObject? Body, long Length, int Status)> ReadJsonAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var maxBodyBytes = options.CurrentValue.MaxRequestBodyBytes;
            if (request.ContentLength > maxBodyBytes)
            {
                throw new BadHttpRequestException("Request body exceeds configured limit.", StatusCodes.Status413PayloadTooLarge);
            }

            // Read into one pooled buffer sized from Content-Length; the parsed tree does not reference it afterwards.
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(request.ContentLength ?? 16 * 1024, 1, maxBodyBytes) + 1);
            var length = 0;
            try
            {
                int read;
                while ((read = await request.Body.ReadAsync(buffer.AsMemory(length), ct)) > 0)
                {
                    length += read;
                    if (length > maxBodyBytes)
                    {
                        throw new BadHttpRequestException("Request body exceeds configured limit.", StatusCodes.Status413PayloadTooLarge);
                    }

                    if (length == buffer.Length)
                    {
                        var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(buffer.Length * 2L, maxBodyBytes + 1));
                        buffer.AsSpan(0, length).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }
                }

                return (JsonNode.Parse(buffer.AsSpan(0, length), documentOptions: JsonOptions) as JsonObject, length, 0);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, 0, 413);
        }
        catch (JsonException)
        {
            return (null, 0, 400);
        }
    }
}
