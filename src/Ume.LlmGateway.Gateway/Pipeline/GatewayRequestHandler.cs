using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// The data-plane pipeline: authenticate → parse → resolve model → rate limit → PII guard → route →
/// reserve budget → call provider(s) with fallback → account. Request and response content is only held
/// in memory for the duration of the call and is never logged or persisted.
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
    private readonly IProviderAdapter[] _adapters = [.. adapters];
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public async Task HandleAsync(HttpContext http, GatewayEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
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
        JsonObject? body;
        long bodyLength;
        AudioUpload? audio = null;
        if (IsAudio(endpoint))
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
                return;
            }

            (body, audio, bodyLength) = (form.Fields, form.Audio, http.Request.ContentLength ?? form.Audio!.Data.Length);
        }
        else if (!http.Request.HasJsonContentType())
        {
            await RejectAsync(http, state, 415, GatewayErrorCodes.UnsupportedMediaType, "Content-Type måste vara application/json.", RequestOutcome.Rejected);
            return;
        }
        else
        {
            (body, bodyLength, var readStatus) = await ReadJsonAsync(http.Request, ct);
            if (readStatus != 0)
            {
                await RejectAsync(http, state, readStatus, readStatus == 413 ? GatewayErrorCodes.RequestTooLarge : GatewayErrorCodes.InvalidRequest,
                    readStatus == 413 ? "Förfrågan är för stor." : "Förfrågan är inte giltig JSON.", RequestOutcome.Rejected);
                return;
            }
        }

        // The upload stays in memory until the last attempt is done; disposing returns the pooled buffer.
        using var audioLifetime = audio;

        if (body?["model"] is not JsonValue modelValue || !modelValue.TryGetValue<string>(out var model) || string.IsNullOrWhiteSpace(model))
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest, "Fältet 'model' saknas. Ange ett modellalias, t.ex. 'ume/chat-standard'.", RequestOutcome.Rejected);
            return;
        }

        state.RequestedModel = model.Length > 200 ? model[..200] : model;

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
            await RejectAsync(http, state, modelRejection.StatusCode, modelRejection.Code, modelRejection.Message, RequestOutcome.Rejected);
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
                http.Response.Headers["x-ume-pii"] = decision switch
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
        var routing = new RoutingDecision([], [], false);
        if (snapshot.Rules.Count > 0)
        {
            if (piiDetected is null && snapshot.Rules.References("pii_detected"))
            {
                piiDetected = PiiJsonScanner.Scan(body, redact: false).HasPii; // signal only: no policy action is taken
            }

            routing = snapshot.Rules.Evaluate(await BuildRoutingContextAsync(http, snapshot, key, endpoint, model, body, estimatedInputTokens, piiDetected, ct), Random.Shared);
        }

        if (routing.Applied.Count > 0)
        {
            state.Rule = routing.Applied[^1];
            http.Response.Headers["x-ume-rule"] = string.Join(',', routing.Applied.Select(a => a.RuleId.ToString("D")));
        }

        var plan = router.Plan(snapshot, key, endpoint, model, requested, routing);
        if (plan.Rejection is { } planRejection)
        {
            await RejectAsync(http, state, planRejection.StatusCode, planRejection.Code, planRejection.Message, RequestOutcome.Rejected);
            return;
        }

        // 7. Candidate providers in attempt order.
        var stream = endpoint != GatewayEndpoint.Embeddings && RequestRewriter.IsStreaming(body);
        state.Streamed = stream;
        var selection = await router.SelectCandidatesAsync(plan.Models, key, endpoint, restrictTo, stream, ct, circuitPrefetch);
        if (selection.Rejection is { } selectionRejection)
        {
            await RejectAsync(http, state, selectionRejection.StatusCode, selectionRejection.Code, selectionRejection.Message, RequestOutcome.Rejected);
            return;
        }

        var candidates = selection.Candidates;

        // 8. Budget reservation across key, team and förvaltning.
        long maxOutput = endpoint == GatewayEndpoint.Embeddings ? 0 : RequestRewriter.RequestedMaxOutputTokens(body) ?? options.CurrentValue.DefaultOutputTokenEstimate;
        var expectedUsage = audio is null ? new TokenUsage(estimatedInputTokens, 0, maxOutput) : audioEstimate;
        var estimate = candidates.Max(t => CostCalculator.Calculate(expectedUsage, t.ModelDeployment!.PriceAt(now), snapshot.SekPerUsd).Sek);
        var reservation = await budgets.ReserveAsync(BudgetService.ApplicableBudgets(snapshot, key), estimate, ct);
        state.Reservation = reservation;
        if (reservation.RemainingSek is { } remainingBefore)
        {
            http.Response.Headers["x-ume-budget-remaining-sek"] = FormatSek(remainingBefore);
        }

        if (!reservation.Allowed)
        {
            var b = reservation.Exceeded!.Budget;
            var scope = b.Scope switch
            {
                BudgetScope.Department => "förvaltningens",
                BudgetScope.Team => "teamets",
                _ => "nyckelns",
            };
            await RejectAsync(http, state, 402, GatewayErrorCodes.BudgetExceeded,
                $"{char.ToUpperInvariant(scope[0])}{scope[1..]} budget ({b.LimitSek:0.##} kr per {PeriodName(b.Period)}) är förbrukad. Kontakta budgetansvarig.",
                RequestOutcome.BudgetExceeded);
            return;
        }

        var clientWantsStreamUsage = (body["stream_options"] as JsonObject)?["include_usage"] is JsonValue iu && iu.TryGetValue<bool>(out var wants) && wants;

        // 9. Attempt candidates in order; fall back on retryable failures before the first byte is sent.
        ProviderFailure? lastFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var deployment = candidates[i].ModelDeployment!;
            var provider = deployment.ProviderAccount!;
            state.Fallbacks = i;

            string? credential;
            try
            {
                credential = credentials.Unprotect(provider.EncryptedCredential);
            }
            catch (CryptographicException)
            {
                LogCredentialUnavailable(logger, provider.Name);
                lastFailure = new ProviderFailure(500, true, "credential_unavailable");
                continue;
            }

            // The rewrite mutates the body; only keep a pristine copy when a fallback attempt may still need it.
            var attemptBody = i == candidates.Count - 1 ? body : (JsonObject)body.DeepClone();
            RequestRewriter.Apply(attemptBody, endpoint, deployment.UpstreamModel, deployment.ParameterProfile, stream);

            ProviderResult result;
            try
            {
                result = await _adapters.Resolve(provider.Type)
                    .SendAsync(new ProviderCall(provider, deployment, endpoint, attemptBody, stream, credential, clientWantsStreamUsage, audio), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await AccountAsync(state, deployment, new TokenUsage(estimatedInputTokens, 0, 0, state.EstimatedAudioSeconds), 499, RequestOutcome.ClientCancelled, null, snapshot);
                return;
            }

            switch (result)
            {
                case ProviderFailure failure when failure.Retryable || IsProviderConfigError(failure.StatusCode):
                    await circuits.RecordFailureAsync(provider.Id, CancellationToken.None);
                    LogProviderFailed(logger, state.RequestId, provider.Name, failure.StatusCode, failure.Reason);
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
                        await circuits.RecordSuccessAsync(provider.Id, CancellationToken.None);
                        var cost = CostCalculator.Calculate(json.Usage, deployment.PriceAt(now), snapshot.SekPerUsd);
                        SetRoutingHeaders(http, deployment, i);
                        http.Response.Headers["x-ume-cost-sek"] = cost.Sek.ToString("0.000000", CultureInfo.InvariantCulture);
                        if (reservation.RemainingSek is { } rem)
                        {
                            http.Response.Headers["x-ume-budget-remaining-sek"] = FormatSek(Math.Max(0, rem - cost.Sek));
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
                        await circuits.RecordSuccessAsync(provider.Id, CancellationToken.None);
                        await StreamAsync(http, state, deployment, i, streamResult, snapshot, ct);
                    }

                    return;
            }
        }

        // 10. Every candidate failed.
        state.Fallbacks = Math.Max(0, candidates.Count - 1);
        http.Response.Headers["x-ume-fallbacks"] = state.Fallbacks.ToString(CultureInfo.InvariantCulture);
        var status = lastFailure?.StatusCode == 504 ? 504 : 502;
        await RejectAsync(http, state, status, GatewayErrorCodes.AllProvidersFailed,
            $"Alla {candidates.Count} leverantörer för '{plan.Models[0].Name}' misslyckades. Senaste fel: {lastFailure?.Reason ?? "okänt"}.",
            RequestOutcome.ProviderError);
    }

    /// <summary>Counts the request against the key's per-minute limits; when refused, the 429 has been written.</summary>
    private async Task<bool> AcquireRateLimitAsync(HttpContext http, RequestState state, VirtualKey key, CancellationToken ct)
    {
        var rate = await rateLimiter.AcquireAsync(key.Id, key.RequestsPerMinute, key.TokensPerMinute, ct);
        if (rate.RequestLimit is { } limit)
        {
            http.Response.Headers["x-ratelimit-limit-requests"] = limit.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["x-ratelimit-remaining-requests"] = Math.Max(0, rate.RequestsRemaining ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        if (rate.Allowed)
        {
            return true;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(rate.RetryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        var what = rate.Reason == "tokens" ? "token per minut" : "anrop per minut";
        await RejectAsync(http, state, 429, GatewayErrorCodes.RateLimited, $"Gränsen för {what} är nådd för nyckeln. Försök igen om {retryAfter} s.", RequestOutcome.RateLimited);
        return false;
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

    public async Task ListModelsAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var key = await AuthenticateAsync(http, GatewayEndpoint.Models, time.GetUtcNow(), ct);
        if (key is null)
        {
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        bool Allowed(string name) => key.AllowedModels.Count == 0 || key.AllowedModels.Contains(name, StringComparer.OrdinalIgnoreCase);
        var residencies = RouteSelector.EffectiveResidencies(key.AllowedResidencies, null);
        bool ResidencyOk(ProviderAccount p) => residencies is null || residencies.Contains(p.Residency);

        var data = new JsonArray();
        foreach (var route in snapshot.Routes.Values.Where(r => r.IsEnabled && Allowed(r.Name)).OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var available = route.Targets.Where(t => t.ModelDeployment is { IsEnabled: true, ProviderAccount: { IsAvailable: true } p } && ResidencyOk(p) && RouteSelector.IsProviderAllowed(p, key.AllowedProviders)).ToList();
            if (available.Count == 0)
            {
                continue;
            }

            data.Add(new JsonObject
            {
                ["id"] = route.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "ume-llm-gateway",
                ["ume"] = new JsonObject
                {
                    ["type"] = "alias",
                    ["kind"] = route.Kind.ToString(),
                    ["description"] = route.Description,
                    ["residencies"] = new JsonArray([.. available.Select(t => (JsonNode?)JsonValue.Create(t.ModelDeployment!.ProviderAccount!.Residency.ToString())).Distinct()]),
                },
            });
        }

        foreach (var deployment in snapshot.Deployments.Values
                     .Where(d => d.IsEnabled && d.ProviderAccount is { IsAvailable: true } && ResidencyOk(d.ProviderAccount) && RouteSelector.IsProviderAllowed(d.ProviderAccount, key.AllowedProviders) && Allowed(d.Name))
                     .OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            data.Add(new JsonObject
            {
                ["id"] = deployment.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = deployment.ProviderAccount!.Name,
                ["ume"] = new JsonObject
                {
                    ["type"] = "model",
                    ["kind"] = deployment.Kind.ToString(),
                    ["residency"] = deployment.ProviderAccount.Residency.ToString(),
                },
            });
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString(), ct);
    }

    private async Task<VirtualKey?> AuthenticateAsync(HttpContext http, GatewayEndpoint endpoint, DateTimeOffset now, CancellationToken ct)
    {
        var docs = options.CurrentValue.DocsUrl;
        var presented = KeyAuthenticator.ExtractKey(http.Request);
        var key = await keys.FindAsync(presented, ct);
        if (key is null)
        {
            authFailures.Record(string.IsNullOrEmpty(presented) ? AuthFailureReason.MissingKey : AuthFailureReason.InvalidKey,
                endpoint, null, http.Connection.RemoteIpAddress);
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await GatewayErrors.WriteAsync(http, endpoint, 401, GatewayErrorCodes.InvalidApiKey,
                "API-nyckeln saknas eller är ogiltig. Skicka den som 'Authorization: Bearer ume-sk-…'.", docs);
            return null;
        }

        var (status, code, message, reason) = key.GetStatus(now) switch
        {
            KeyStatus.Revoked => (401, GatewayErrorCodes.KeyRevoked, "API-nyckeln är återkallad. Använd den nya nyckeln eller be om en ny.", AuthFailureReason.KeyRevoked),
            KeyStatus.Expired => (401, GatewayErrorCodes.KeyExpired, "API-nyckeln har gått ut. Be administratören att rotera den.", AuthFailureReason.KeyExpired),
            KeyStatus.Disabled => (403, GatewayErrorCodes.KeyDisabled, "API-nyckeln är tillfälligt inaktiverad.", AuthFailureReason.KeyDisabled),
            _ when key.Team is not { IsActive: true } || key.Team.Department is not { IsActive: true }
                => (403, GatewayErrorCodes.KeyDisabled, "Teamet eller förvaltningen som äger nyckeln är inaktiverad.", AuthFailureReason.OwnerInactive),
            _ => (0, string.Empty, string.Empty, default),
        };

        if (status != 0)
        {
            authFailures.Record(reason, endpoint, key, http.Connection.RemoteIpAddress);
            await GatewayErrors.WriteAsync(http, endpoint, status, code, message, docs);
            return null;
        }

        return key;
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
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // Upstream broke mid-stream; headers are already sent, so no fallback is possible.
            LogStreamBroken(logger, state.RequestId, deployment.ProviderAccount!.Name, ex.GetType().Name);
            status = 502;
            outcome = RequestOutcome.ProviderError;
            errorCode = "stream_interrupted";
        }

        await AccountAsync(state, deployment, result.Usage.ToTokenUsage(state.EstimatedInputTokens, state.EstimatedAudioSeconds), status, outcome, errorCode, snapshot);
    }

    private async Task RejectAsync(HttpContext http, RequestState state, int status, string code, string message, RequestOutcome outcome)
    {
        await GatewayErrors.WriteAsync(http, state.Endpoint, status, code, message, options.CurrentValue.DocsUrl);
        await AccountAsync(state, null, default, status, outcome, code, null);
    }

    /// <summary>
    /// Reconciles the budget reservation, records rate-limit tokens and queues the usage record. The cost is calculated
    /// from <paramref name="usage"/> at the deployment's price unless given (live sessions with two priced models).
    /// </summary>
    private async Task AccountAsync(RequestState state, ModelDeployment? deployment, TokenUsage usage, int status, RequestOutcome outcome, string? errorCode, CatalogSnapshot? snapshot, Cost? knownCost = null)
    {
        var key = state.Key!;
        var now = time.GetUtcNow();
        var cost = knownCost ?? (deployment is not null && snapshot is not null
            ? CostCalculator.Calculate(usage, deployment.PriceAt(now), snapshot.SekPerUsd)
            : default);

        // Accounting must complete even if the client disconnected.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IReadOnlyList<AlertCandidate> alerts = [];
        try
        {
            // Issued together so a shared store pipelines them into a single round trip.
            var commit = state.Reservation is { } reservation ? budgets.CommitAsync(reservation, cost.Sek, cts.Token) : Task.FromResult(alerts);
            var tokens = usage.Total > 0 ? rateLimiter.RecordTokensAsync(key.Id, usage.Total, cts.Token) : Task.CompletedTask;
            await Task.WhenAll(commit, tokens);
            alerts = await commit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAccountingFailed(logger, ex, state.RequestId);
        }

        var record = new UsageRecord
        {
            RequestId = state.RequestId,
            Timestamp = now,
            VirtualKeyId = key.Id,
            TeamId = key.TeamId,
            DepartmentId = key.Team?.DepartmentId ?? Guid.Empty,
            Endpoint = state.Endpoint,
            RequestedModel = state.RequestedModel ?? string.Empty,
            ProviderAccountId = deployment?.ProviderAccountId,
            ProviderName = deployment?.ProviderAccount?.Name,
            ModelDeploymentId = deployment?.Id,
            UpstreamModel = deployment?.UpstreamModel,
            InputTokens = usage.InputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            OutputTokens = usage.OutputTokens,
            AudioSeconds = decimal.Round(usage.AudioSeconds, 3),
            CostUsd = decimal.Round(cost.Usd, 6),
            CostSek = decimal.Round(cost.Sek, 6),
            LatencyMs = (int)Math.Min(int.MaxValue, time.GetElapsedTime(state.StartTimestamp).TotalMilliseconds),
            StatusCode = status,
            Outcome = outcome,
            FallbackCount = state.Fallbacks,
            Streamed = state.Streamed,
            PiiActionApplied = state.PiiAction,
            PiiCategories = state.PiiCategories,
            ErrorCode = errorCode,
            RoutingRuleId = state.Rule?.RuleId,
            RoutingRuleName = state.Rule?.Name is { Length: > 200 } ruleName ? ruleName[..200] : state.Rule?.Name,
        };

        metrics.Record(record);
        LogCompleted(logger, state.RequestId, key.Prefix, record.RequestedModel, record.ProviderName ?? "-", status, record.LatencyMs, state.Fallbacks);
        if (state.PiiAction is { } piiAction)
        {
            SecurityEvents.PiiAction(_security, PiiActionName(piiAction), state.RequestId, state.Endpoint.ToString(), state.PiiCategories,
                key.Id, key.Prefix, key.TeamId, record.DepartmentId);
        }

        if (errorCode is GatewayErrorCodes.AttachmentNotAllowed or GatewayErrorCodes.ModelNotAllowed)
        {
            SecurityEvents.RequestRefused(_security, state.RequestId, state.Endpoint.ToString(), errorCode, key.Id, key.Prefix, key.TeamId, record.DepartmentId);
        }
        try
        {
            await usageWriter.EnqueueAsync(new UsageWork(record, alerts), cts.Token);
        }
        catch (OperationCanceledException)
        {
            LogUsageDropped(logger, state.RequestId);
        }
    }

    private static readonly HashSet<string> HiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "cookie", "set-cookie",
    };

    /// <summary>Request fields that carry prompt content; never exposed to routing conditions.</summary>
    private static readonly HashSet<string> ContentFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "messages", "input", "prompt", "system", "instructions", "tools", "contents",
    };

    /// <summary>
    /// What routing conditions may see. Credentials and prompt content are never included, and the budget and token
    /// lookups only happen when an enabled rule mentions them.
    /// </summary>
    private async Task<RoutingContext> BuildRoutingContextAsync(HttpContext http, CatalogSnapshot snapshot, VirtualKey key, GatewayEndpoint endpoint, string model, JsonObject body, long promptTokens, bool? piiDetected, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in http.Request.Headers)
        {
            if (!HiddenHeaders.Contains(name))
            {
                var text = value.ToString();
                headers[name] = text.Length > 512 ? text[..512] : text;
            }
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in body)
        {
            if (node is JsonValue scalar && !ContentFields.Contains(name))
            {
                parameters[name] = scalar.TryGetValue<bool>(out var flag) ? flag
                    : scalar.TryGetValue<double>(out var number) ? number
                    : scalar.TryGetValue<string>(out var text) ? (text.Length > 512 ? text[..512] : text)
                    : null;
            }
        }

        double? budgetUsed = null;
        if (snapshot.Rules.References("budget_used"))
        {
            budgetUsed = await budgets.PeekUsedPercentAsync(BudgetService.ApplicableBudgets(snapshot, key), ct);
        }

        double? tokensUsed = null;
        if (snapshot.Rules.References("tokens_used"))
        {
            tokensUsed = await rateLimiter.PeekTokensUsedPercentAsync(key.Id, key.TokensPerMinute, ct);
        }

        return new RoutingContext
        {
            Model = model,
            Endpoint = RouteResolver.EndpointName(endpoint),
            Headers = headers,
            Params = parameters,
            KeyId = key.Id,
            KeyLineage = snapshot.KeyLineage(key.Id),
            KeyName = key.Name,
            TeamId = key.TeamId,
            TeamName = key.Team?.Name,
            DepartmentId = key.Team?.DepartmentId,
            DepartmentName = key.Team?.Department?.Name,
            BudgetUsed = budgetUsed,
            TokensUsed = tokensUsed,
            PiiDetected = piiDetected,
            PromptTokens = promptTokens,
        };
    }

    private static void SetRoutingHeaders(HttpContext http, ModelDeployment deployment, int fallbacks)
    {
        http.Response.Headers["x-ume-provider"] = deployment.ProviderAccount!.Name;
        http.Response.Headers["x-ume-model"] = deployment.Name;
        http.Response.Headers["x-ume-residency"] = deployment.ProviderAccount.Residency.ToString();
        http.Response.Headers["x-ume-fallbacks"] = fallbacks.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsAudio(GatewayEndpoint endpoint) => endpoint is GatewayEndpoint.AudioTranscriptions or GatewayEndpoint.AudioTranslations;

    /// <summary>Upstream 401/403/404 means our provider configuration is wrong, not the client's request: try the next provider.</summary>
    private static bool IsProviderConfigError(int status) => status is 401 or 403 or 404;

    private static readonly (AttachmentKinds Kind, string Name)[] AttachmentKindNames =
        [(AttachmentKinds.Image, "bilder"), (AttachmentKinds.Document, "dokument"), (AttachmentKinds.Audio, "ljudfiler"), (AttachmentKinds.Video, "videofiler")];

    private static string AttachmentNames(AttachmentKinds kinds) =>
        string.Join(" eller ", AttachmentKindNames.Where(n => kinds.HasFlag(n.Kind)).Select(n => n.Name));

    /// <summary>The <c>Action</c> attribute of the <c>gateway.pii.action</c> security event.</summary>
    private static string PiiActionName(PiiPolicy policy) => policy switch
    {
        PiiPolicy.Block => "blocked",
        PiiPolicy.Redact => "redacted",
        PiiPolicy.RerouteToOnPrem => "rerouted_onprem",
        _ => "detected",
    };

    private static string FormatSek(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string PeriodName(BudgetPeriod period) => period switch
    {
        BudgetPeriod.Hourly => "timme",
        BudgetPeriod.Daily => "dag",
        BudgetPeriod.Weekly => "vecka",
        BudgetPeriod.Monthly => "månad",
        BudgetPeriod.Quarterly => "kvartal",
        _ => "år",
    };

    private sealed class RequestState(string requestId, GatewayEndpoint endpoint, long startTimestamp)
    {
        public string RequestId { get; } = requestId;
        public GatewayEndpoint Endpoint { get; } = endpoint;
        public long StartTimestamp { get; } = startTimestamp;
        public VirtualKey? Key { get; set; }
        public string? RequestedModel { get; set; }
        public long EstimatedInputTokens { get; set; }
        public decimal EstimatedAudioSeconds { get; set; }
        public bool Streamed { get; set; }
        public int Fallbacks { get; set; }
        public PiiPolicy? PiiAction { get; set; }
        public string? PiiCategories { get; set; }
        public BudgetReservation? Reservation { get; set; }
        public AppliedRule? Rule { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {RequestId} key {KeyPrefix} model {Model} provider {Provider} status {Status} in {LatencyMs} ms (fallbacks {Fallbacks})")]
    private static partial void LogCompleted(ILogger logger, string requestId, string keyPrefix, string model, string provider, int status, int latencyMs, int fallbacks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: provider {Provider} failed with {Status} ({Reason}); trying next")]
    private static partial void LogProviderFailed(ILogger logger, string requestId, string provider, int status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: stream from {Provider} interrupted ({Error})")]
    private static partial void LogStreamBroken(ILogger logger, string requestId, string provider, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Credential for provider {Provider} cannot be decrypted (Data Protection key ring missing?)")]
    private static partial void LogCredentialUnavailable(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Error, Message = "Accounting failed for request {RequestId}")]
    private static partial void LogAccountingFailed(ILogger logger, Exception ex, string requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Usage record for request {RequestId} could not be queued")]
    private static partial void LogUsageDropped(ILogger logger, string requestId);
}
