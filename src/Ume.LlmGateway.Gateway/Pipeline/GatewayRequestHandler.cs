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
    GatewayCatalog catalog,
    IRouteResolver router,
    IRateLimiter rateLimiter,
    BudgetService budgets,
    ICircuitBreakerStore circuits,
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
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    /// <summary>Bodies this large are walked for images even when the key allows all files (see the token estimate).</summary>
    private const long AttachmentInspectBytes = 32 * 1024;
    private const string MissingModelMessage = "Fältet 'model' saknas. Ange ett modellalias, t.ex. 'ume/chat-standard'.";
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
        var (body, bodyLength, audio) = await ReadBodyAsync(http, state, info, ct);
        if (body is null)
        {
            return;
        }

        // The upload stays in memory until the last attempt is done; disposing returns the pooled buffer.
        using var audioLifetime = audio;

        if (body["model"] is not JsonValue modelValue || !modelValue.TryGetValue<string>(out var model) || string.IsNullOrWhiteSpace(model))
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest, MissingModelMessage, RequestOutcome.Rejected);
            return;
        }

        state.RequestedModel = ModelNames.Truncate(model);

        // 2b. Attachment policy (per key): files are base64 the PII guard cannot read, so sensitive keys may refuse them.
        //     Large bodies are inspected regardless, so inline images do not count as text in the token estimate.
        var attachments = audio is not null ? new AttachmentScan(AttachmentKinds.Audio, 0, 0)
            : key.AttachmentPolicy != AttachmentPolicy.Allowed || bodyLength >= AttachmentInspectBytes ? AttachmentScanner.Inspect(body)
            : default;
        if (AttachmentScanner.Disallowed(key.AttachmentPolicy, attachments.Kinds) is var refused and not AttachmentKinds.None)
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.AttachmentNotAllowed,
                $"Nyckeln tillåter inte bifogade {AttachmentNames(refused)}{(key.AttachmentPolicy == AttachmentPolicy.ImagesOnly ? " (endast bilder är tillåtna)" : string.Empty)}. Skicka förfrågan utan filer.",
                RequestOutcome.Rejected);
            return;
        }

        // 3. Resolve model alias and check the key's allow-list and endpoint compatibility.
        var snapshot = await catalog.GetAsync(ct);
        var resolution = router.ResolveModel(snapshot, key, endpoint, model);
        if (resolution.Rejection is { } modelRejection)
        {
            await RejectAsync(http, state, modelRejection);
            return;
        }

        var requested = resolution.Model;
        // Issued now so it is pipelined with the rate-limit call below instead of costing its own round trip.
        var circuitPrefetch = requested is null ? null : router.PrefetchCircuits(requested, ct);

        // 4. Rate limits (requests and tokens per minute).
        // Audio: from the file's playing time (both the per-minute and the per-token way of pricing speech models).
        var audioEstimate = audio is null ? default : CostCalculator.EstimateTranscription(audio.EstimatedSeconds);
        var estimatedInputTokens = audio is null ? CostCalculator.EstimateInputTokens(bodyLength, attachments) : audioEstimate.InputTokens;
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
                await RejectAsync(http, state, 400, GatewayErrorCodes.PiiBlocked,
                    $"Förfrågan innehåller personuppgifter ({scan.Summary}) och nyckelns policy tillåter inte det. Ta bort uppgifterna och försök igen.",
                    RequestOutcome.PiiBlocked);
                return;
            }

            if (decision == PiiDecision.ForwardOnPremOnly)
            {
                restrictTo = DataResidency.OnPrem;
            }
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
        var route = await SelectRouteAsync(http, state, snapshot, key, model, requested, routing, restrictTo, stream, circuitPrefetch, ct);
        if (route is not { } selected)
        {
            return;
        }

        // 8. Budget reservation across key, team and förvaltning.
        long maxOutput = info.HasOutputTokens ? RequestRewriter.RequestedMaxOutputTokens(body) ?? options.CurrentValue.DefaultOutputTokenEstimate : 0;
        var expectedUsage = audio is null ? new TokenUsage(estimatedInputTokens, 0, maxOutput) : audioEstimate;
        var estimate = selected.Candidates.Max(t => CostCalculator.Calculate(expectedUsage, t.ModelDeployment!.PriceAt(now), snapshot.SekPerUsd).Sek);
        if (await TryReserveAsync(http, state, snapshot, BudgetService.ApplicableBudgets(snapshot, key), estimate, ct) is null)
        {
            return;
        }

        // 9. Attempt candidates in order. From here on the reservation must be reconciled whatever happens.
        try
        {
            await AttemptAsync(http, state, snapshot, body, audio, selected, ct);
        }
        catch (Exception ex) when (!state.Accounted)
        {
            if (!await AccountUnexpectedAsync(http, state, snapshot, ex))
            {
                throw;
            }
        }
    }

    /// <summary>Calls the candidates in order, falling back while nothing has been sent to the client, and accounts the outcome.</summary>
    private async Task AttemptAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, JsonObject body, AudioUpload? audio,
        SelectedRoute route, CancellationToken ct)
    {
        var candidates = route.Candidates;
        var now = time.GetUtcNow();
        var clientWantsStreamUsage = (body["stream_options"] as JsonObject)?["include_usage"] is JsonValue iu && iu.TryGetValue<bool>(out var wants) && wants;
        ProviderFailure? lastFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var deployment = candidates[i].ModelDeployment!;
            var provider = deployment.ProviderAccount!;
            state.Fallbacks = i;
            state.Deployment = deployment;

            if (!TryGetCredential(snapshot, provider, out var credential))
            {
                lastFailure = ProviderFailure.CredentialUnavailable;
                continue;
            }

            // The rewrite mutates the body; only keep a pristine copy when a fallback attempt may still need it.
            var attemptBody = i == candidates.Count - 1 ? body : (JsonObject)body.DeepClone();
            RequestRewriter.Apply(attemptBody, state.Endpoint, deployment.UpstreamModel, deployment.ParameterProfile, state.Streamed);

            ProviderResult result;
            try
            {
                result = await _adapters.Resolve(provider.Type)
                    .SendAsync(new ProviderCall(provider, deployment, state.Endpoint, attemptBody, state.Streamed, credential, clientWantsStreamUsage, audio), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await AccountAsync(state, deployment, new TokenUsage(state.EstimatedInputTokens, 0, 0, state.EstimatedAudioSeconds), 499, RequestOutcome.ClientCancelled, null, snapshot);
                return;
            }

            switch (result)
            {
                case ProviderFailure failure when failure.CanFallBack:
                    await RecordFailureAsync(state, provider, failure);
                    lastFailure = failure;
                    continue;

                case ProviderFailure failure:
                    // Client error (e.g. invalid parameter): retrying elsewhere would fail the same way.
                    SetRoutingHeaders(http, deployment, i);
                    http.Response.StatusCode = failure.StatusCode;
                    http.Response.ContentType = failure.ContentType ?? "application/json";
                    if (failure.Body is not null)
                    {
                        await http.Response.WriteAsync(failure.Body, ct);
                    }

                    await AccountAsync(state, deployment, default, failure.StatusCode, RequestOutcome.ProviderError, GatewayErrorCodes.ProviderRejected, snapshot);
                    return;

                case ProviderJsonResult json:
                    using (json)
                    using (usageWriter.TrackPending())
                    {
                        await RecordSuccessAsync(state, provider);
                        var cost = CostCalculator.Calculate(json.Usage, deployment.PriceAt(now), snapshot.SekPerUsd);
                        SetRoutingHeaders(http, deployment, i);
                        http.Response.Headers[GatewayHeaders.CostSek] = cost.Sek.ToString("0.000000", CultureInfo.InvariantCulture);
                        if (state.Reservation?.RemainingSek is { } rem)
                        {
                            http.Response.Headers[GatewayHeaders.BudgetRemainingSek] = FormatSek(Math.Max(0, rem - cost.Sek));
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
                        await StreamAsync(http, state, deployment, i, streamResult, snapshot, ct);
                    }

                    return;
            }
        }

        // 10. Every candidate failed.
        await RejectAllFailedAsync(http, state, candidates.Count, route.ModelName, lastFailure);
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
        try
        {
            await http.Response.StartAsync(ct);
            var writer = http.Response.BodyWriter;
            await foreach (var evt in result.Events.WithCancellation(ct))
            {
                // Encoded straight into the response pipe; one flush per event keeps tokens flowing immediately.
                evt.WriteTo(writer);
                await writer.FlushAsync(ct);
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

        await AccountAsync(state, deployment, result.Usage.ToTokenUsage(state.EstimatedInputTokens, state.EstimatedAudioSeconds), status, outcome, errorCode, snapshot);
    }

    /// <summary>
    /// The request body: a JSON object, or the form fields and file of an audio upload. A null body means the request was
    /// refused (the answer has been written and accounted).
    /// </summary>
    private async Task<(JsonObject? Body, long Length, AudioUpload? Audio)> ReadBodyAsync(HttpContext http, RequestState state, GatewayEndpointInfo info, CancellationToken ct)
    {
        if (info.IsAudio)
        {
            var maxAudioBytes = options.CurrentValue.MaxAudioRequestBodyBytes;
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
            {
                sizeLimit.MaxRequestBodySize = maxAudioBytes;
            }

            var form = await AudioFormReader.ReadAsync(http.Request, maxAudioBytes, ct);
            if (form.Failed)
            {
                await RejectAsync(http, state, form.Status, form.Code!, form.Message!, RequestOutcome.Rejected);
                return default;
            }

            return (form.Fields ?? new JsonObject(), http.Request.ContentLength ?? form.Audio!.Data.Length, form.Audio);
        }

        if (!http.Request.HasJsonContentType())
        {
            await RejectAsync(http, state, 415, GatewayErrorCodes.UnsupportedMediaType, "Content-Type måste vara application/json.", RequestOutcome.Rejected);
            return default;
        }

        var (body, length, status) = await ReadJsonAsync(http.Request, ct);
        if (status != 0)
        {
            await RejectAsync(http, state, status, status == 413 ? GatewayErrorCodes.RequestTooLarge : GatewayErrorCodes.InvalidRequest,
                status == 413 ? "Förfrågan är för stor." : "Förfrågan är inte giltig JSON.", RequestOutcome.Rejected);
            return default;
        }

        if (body is null)
        {
            // Valid JSON, but not an object.
            await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest, MissingModelMessage, RequestOutcome.Rejected);
            return default;
        }

        return (body, length, null);
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
