using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline.Realtime;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Live audio (OpenAI Realtime protocol over WebSocket). The usual pipeline runs once, at connect: key, attachment
/// policy, model, rate limit, routing rules, candidates and a budget reservation; the provider connection is opened
/// before the client's handshake is accepted, so every refusal (and every fallback) happens as a plain HTTP answer.
/// The session is then relayed, metered and checked against its budgets until either side closes it, and accounted
/// as one usage record.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>Client headers passed on to the provider: the protocol version of beta Realtime clients.</summary>
    private static readonly string[] RealtimeHeaders = ["OpenAI-Beta"];

    public async Task HandleRealtimeAsync(HttpContext http, GatewayEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var state = new RequestState(RequestIdMiddleware.Get(http), endpoint, time.GetTimestamp()) { Streamed = true };
        var now = time.GetUtcNow();
        var settings = options.CurrentValue.Realtime;

        var key = await AuthenticateAsync(http, endpoint, now, ct);
        if (key is null)
        {
            return;
        }

        state.Key = key;
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest,
                "Endpointen kräver en WebSocket-anslutning: wss://…/v1/realtime?model=<alias>.", RequestOutcome.Rejected);
            return;
        }

        var model = http.Request.Query["model"].ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest,
                "Parametern 'model' saknas. Anslut med ?model=<alias>, t.ex. 'ume/live-transcribe'.", RequestOutcome.Rejected);
            return;
        }

        state.RequestedModel = ModelNames.Truncate(model);
        if (AttachmentScanner.Disallowed(key.AttachmentPolicy, AttachmentKinds.Audio) is var refused and not AttachmentKinds.None)
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.AttachmentNotAllowed,
                $"Nyckeln tillåter inte {AttachmentNames(refused)}, och en realtidssession skickar ljud.", RequestOutcome.Rejected);
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        var resolution = router.ResolveModel(snapshot, key, endpoint, model);
        if (resolution.Rejection is { } modelRejection)
        {
            await RejectAsync(http, state, modelRejection);
            return;
        }

        var circuitPrefetch = resolution.Model is null ? null : router.PrefetchCircuits(resolution.Model, ct);
        if (!await AcquireRateLimitAsync(http, state, key, ct))
        {
            return;
        }

        // Routing rules see the query parameters (never credentials); there is no content to scan at connect.
        var query = http.Request.Query
            .Where(q => !q.Key.Equals("model", StringComparison.OrdinalIgnoreCase))
            .Select(q => KeyValuePair.Create(q.Key, q.Value.ToString()))
            .ToList();
        var routing = snapshot.Rules.Count == 0
            ? RoutingDecision.Empty
            : await ApplyRoutingAsync(http, state, snapshot, key, model, QueryParameters(query), 0, false, ct);

        var route = await SelectRouteAsync(http, state, snapshot, key, model, resolution.Model, routing, null, false, circuitPrefetch, ct);
        if (route is not { } selected)
        {
            return;
        }

        // A slot per open session, shared across instances; it expires by itself if this instance dies.
        if (!await TryOpenSessionAsync(key.Id, state.RequestId, settings.MaxSessionsPerKey, settings.SessionSlotLifetime, ct))
        {
            await RejectAsync(http, state, 429, GatewayErrorCodes.RateLimited,
                $"Nyckeln har redan {settings.MaxSessionsPerKey} öppna realtidssessioner. Stäng en session och försök igen.", RequestOutcome.RateLimited);
            return;
        }

        try
        {
            await RunRealtimeAsync(http, state, key, snapshot, selected, query, settings, ct);
        }
        finally
        {
            try
            {
                await sessions.CloseAsync(key.Id, state.RequestId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSessionRegistryFailed(logger, ex, state.RequestId);
            }
        }
    }

    private async Task RunRealtimeAsync(HttpContext http, RequestState state, VirtualKey key, CatalogSnapshot snapshot,
        SelectedRoute route, List<KeyValuePair<string, string>> query, RealtimeOptions settings, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var endpoint = state.Endpoint;
        var candidates = route.Candidates;

        // Budget: a block of minutes is reserved now and topped up while the session runs.
        var block = CostCalculator.EstimateRealtime(settings.ReserveMinutes * 60m);
        var estimate = candidates.Max(t => CostCalculator.Calculate(block, t.ModelDeployment!.PriceAt(now), snapshot.SekPerUsd).Sek);
        var applicable = BudgetService.ApplicableBudgets(snapshot, key);
        var reservation = await TryReserveAsync(http, state, snapshot, applicable, estimate, ct);
        if (reservation is null)
        {
            return;
        }

        // From here on the reservations must be reconciled whatever happens.
        var extra = new List<BudgetReservation>();
        RealtimeMeter? meter = null;
        RealtimeEventPolicy? policy = null;
        ModelPrice? price = null;
        try
        {
            // Connect upstream with fallback, before accepting the client's handshake.
            var headers = RealtimeHeaders
                .Where(h => http.Request.Headers.ContainsKey(h))
                .Select(h => KeyValuePair.Create(h, http.Request.Headers[h].ToString()))
                .ToList();
            WebSocket? upstream = null;
            ModelDeployment? deployment = null;
            ProviderFailure? lastFailure = null;
            for (var i = 0; i < candidates.Count && upstream is null; i++)
            {
                var target = candidates[i].ModelDeployment!;
                var provider = target.ProviderAccount!;
                state.Fallbacks = i;
                state.Deployment = target;
                if (!TryGetCredential(snapshot, provider, out var credential))
                {
                    lastFailure = ProviderFailure.CredentialUnavailable;
                    continue;
                }

                WebSocket? socket;
                ProviderFailure? failure;
                try
                {
                    (socket, failure) = await realtime.ConnectAsync(new RealtimeCall(provider, target, endpoint, credential, query, headers), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await AccountAsync(state, target, default, 499, RequestOutcome.ClientCancelled, null, snapshot);
                    return;
                }

                if (failure is null)
                {
                    (upstream, deployment) = (socket, target);
                    break;
                }

                if (!failure.CanFallBack)
                {
                    SetRoutingHeaders(http, target, i);
                    await GatewayErrors.WriteAsync(http, endpoint, failure.StatusCode, GatewayErrorCodes.ProviderRejected,
                        $"Leverantören avvisade sessionen ({failure.StatusCode}). Kontrollera parametrarna i anslutningsadressen.", options.CurrentValue.DocsUrl);
                    await AccountAsync(state, target, default, failure.StatusCode, RequestOutcome.ProviderError, GatewayErrorCodes.ProviderRejected, snapshot);
                    return;
                }

                await RecordFailureAsync(state, provider, failure);
                lastFailure = failure;
            }

            if (upstream is null || deployment is null)
            {
                await RejectAllFailedAsync(http, state, candidates.Count, route.ModelName, lastFailure);
                return;
            }

            using var upstreamLifetime = upstream;
            var account = deployment.ProviderAccount!;
            SetRoutingHeaders(http, deployment, state.Fallbacks);
            WebSocket client;
            try
            {
                client = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    SubProtocol = http.WebSockets.WebSocketRequestedProtocols.Contains("realtime") ? "realtime" : null,
                    KeepAliveInterval = TimeSpan.FromSeconds(30),
                });
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException)
            {
                await AccountAsync(state, deployment, default, 499, RequestOutcome.ClientCancelled, null, snapshot);
                return;
            }

            using var clientLifetime = client;
            await RecordSuccessAsync(state, account);

            // An input transcription model the client picks must be one the key may use, on this provider, and eligible.
            var transcription = new RoutingConstraints(endpoint, RouteSelector.EffectiveResidencies(key.AllowedResidencies, null), null, key.AllowedProviders);
            var sessionPolicy = policy = new RealtimeEventPolicy(deployment, name => SameProviderTranscription(snapshot, key, account, transcription, name),
                key.PiiPolicy, account.Residency == DataResidency.OnPrem);
            var sessionMeter = meter = new RealtimeMeter();
            var sessionPrice = price = deployment.PriceAt(now);
            var started = time.GetTimestamp();
            var reservedSek = estimate;
            var blockSek = CostCalculator.Calculate(block, sessionPrice, snapshot.SekPerUsd).Sek;
            var intervalSek = CostCalculator.Calculate(CostCalculator.EstimateRealtime(settings.BudgetCheckSeconds), sessionPrice, snapshot.SekPerUsd).Sek;

            async Task<RealtimeStop?> CheckAsync(CancellationToken token)
            {
                try
                {
                    await sessions.RenewAsync(key.Id, state.RequestId, settings.SessionSlotLifetime, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSessionRegistryFailed(logger, ex, state.RequestId);
                }

                if (time.GetElapsedTime(started) >= TimeSpan.FromMinutes(settings.MaxSessionMinutes))
                {
                    return new RealtimeStop(RealtimeEndReason.TimeLimit, "session_time_limit",
                        $"Sessionen har nått maxlängden {settings.MaxSessionMinutes} minuter. Anslut igen för att fortsätta.", WebSocketCloseStatus.NormalClosure);
                }

                if (reservation.Checks.Count == 0 || SessionCost(sessionMeter, sessionPolicy, sessionPrice, snapshot).Sek + intervalSek <= reservedSek)
                {
                    return null;
                }

                try
                {
                    var more = await budgets.ReserveAsync(applicable, blockSek, token);
                    if (!more.Allowed)
                    {
                        return new RealtimeStop(RealtimeEndReason.BudgetExceeded, GatewayErrorCodes.BudgetExceeded,
                            BudgetExceededMessage(more.Exceeded!.Budget, "Sessionen avslutas."), WebSocketCloseStatus.PolicyViolation);
                    }

                    extra.Add(more);
                    reservedSek += blockSek;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The shared store is briefly unavailable: keep the session and try again at the next check.
                    LogAccountingFailed(logger, ex, state.RequestId);
                }

                return null;
            }

            RealtimeEndReason reason;
            metrics.SessionOpened(endpoint);
            using (var relay = new RealtimeRelay(client, upstream, sessionPolicy, sessionMeter, settings.MaxMessageBytes, logger))
            {
                try
                {
                    reason = await relay.RunAsync(TimeSpan.FromSeconds(settings.BudgetCheckSeconds), CheckAsync, lifetime.ApplicationStopping, ct);
                }
                finally
                {
                    metrics.SessionClosed(endpoint);
                }
            }

            // The extra blocks are released; the first reservation is reconciled to the session's cost below.
            await ReleaseAsync(extra, state.RequestId);
            state.PiiAction = sessionPolicy.PiiApplied;
            state.PiiCategories = sessionPolicy.PiiCategories;
            var (status, outcome, errorCode) = reason switch
            {
                RealtimeEndReason.ClientLost => (499, RequestOutcome.ClientCancelled, (string?)null),
                RealtimeEndReason.ProviderFailed => (502, RequestOutcome.ProviderError, "stream_interrupted"),
                RealtimeEndReason.BudgetExceeded => (402, RequestOutcome.BudgetExceeded, GatewayErrorCodes.BudgetExceeded),
                RealtimeEndReason.TimeLimit => (200, RequestOutcome.Success, "session_time_limit"),
                RealtimeEndReason.Shutdown => (200, RequestOutcome.Success, "gateway_shutting_down"),
                _ => (200, RequestOutcome.Success, null),
            };

            await AccountAsync(state, deployment, SessionUsage(sessionMeter), status, outcome, errorCode, snapshot,
                SessionCost(sessionMeter, sessionPolicy, sessionPrice, snapshot));
        }
        catch (Exception ex) when (!state.Accounted)
        {
            // Bill what the session used before it broke, then let the shared handling answer or abort.
            await ReleaseAsync(extra, state.RequestId);
            var used = meter is null ? default : SessionUsage(meter);
            Cost? cost = meter is null || policy is null ? null : SessionCost(meter, policy, price, snapshot);
            if (!await AccountUnexpectedAsync(http, state, snapshot, ex, used, cost))
            {
                throw;
            }
        }
    }

    /// <summary>The session's usage: audio duration is the session's; the input transcription model only adds its tokens (and its own price).</summary>
    private static TokenUsage SessionUsage(RealtimeMeter meter)
    {
        var (session, transcription) = meter.Snapshot();
        return session + (transcription with { AudioSeconds = 0 });
    }

    /// <summary>Releases the extra budget blocks a session reserved while it ran. Never throws.</summary>
    private async Task ReleaseAsync(List<BudgetReservation> extra, string requestId)
    {
        if (extra.Count == 0)
        {
            return;
        }

        using var release = new CancellationTokenSource(AccountingTimeout);
        foreach (var more in extra)
        {
            try
            {
                await budgets.CommitAsync(more, 0, release.Token);
            }
            catch (OperationCanceledException)
            {
                // Only our own timeout is passed in: give up on the rest, the counters expire with their period.
                LogAccountingTimedOut(logger, requestId);
                break;
            }
            catch (Exception ex)
            {
                LogAccountingFailed(logger, ex, requestId);
            }
        }

        extra.Clear();
    }

    private static JsonObject QueryParameters(List<KeyValuePair<string, string>> query)
    {
        var parameters = new JsonObject();
        foreach (var (name, value) in query)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    /// <summary>Cost so far: the session model at its price, plus a separately chosen input transcription model at its own.</summary>
    private Cost SessionCost(RealtimeMeter meter, RealtimeEventPolicy policy, ModelPrice? price, CatalogSnapshot snapshot)
    {
        var (session, transcription) = meter.Snapshot();
        var cost = CostCalculator.Calculate(session, price, snapshot.SekPerUsd);
        if (policy.TranscriptionDeployment is { } other)
        {
            var extra = CostCalculator.Calculate(transcription, other.PriceAt(time.GetUtcNow()), snapshot.SekPerUsd);
            cost = new Cost(cost.Usd + extra.Usd, cost.Sek + extra.Sek);
        }

        return cost;
    }

    /// <summary>
    /// The input transcription model a conversation session asks for in <c>session.update</c>: a speech-to-text alias or
    /// model the key may use, served by the session's provider (the audio is already there) and eligible like any route
    /// target (enabled, provider available, within the key's provider and residency allow-lists).
    /// </summary>
    private static ModelDeployment? SameProviderTranscription(CatalogSnapshot snapshot, VirtualKey key, ProviderAccount provider, RoutingConstraints constraints, string name)
    {
        var resolved = snapshot.Resolve(name);
        if (resolved is not { Kind: ModelKind.Transcription } || !key.IsModelAllowed(resolved.Name))
        {
            return null;
        }

        foreach (var target in resolved.Targets)
        {
            if (target.ModelDeployment is { } deployment && deployment.ProviderAccountId == provider.Id && RouteSelector.IsEligible(deployment, constraints))
            {
                return deployment;
            }
        }

        return null;
    }

    private async Task<bool> TryOpenSessionAsync(Guid keyId, string sessionId, int maxSessions, TimeSpan slotLifetime, CancellationToken ct)
    {
        if (maxSessions <= 0)
        {
            return true;
        }

        try
        {
            return await sessions.TryOpenAsync(keyId, sessionId, maxSessions, slotLifetime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. a Redis ACL from before live audio: count nothing rather than refuse every session.
            LogSessionRegistryFailed(logger, ex, sessionId);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime session registry unavailable for {RequestId}; the per-key session limit is not enforced")]
    private static partial void LogSessionRegistryFailed(ILogger logger, Exception ex, string requestId);
}
