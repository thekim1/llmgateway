using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.ContentGuard;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Usage;
using Ume.LlmGateway.Gateway.Budgets;
using Ume.LlmGateway.Gateway.Realtime;
using Ume.LlmGateway.Gateway.Routing;
using Ume.LlmGateway.Gateway.Usage;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// Live audio (OpenAI Realtime protocol over WebSocket). The usual pipeline runs once, at connect: key, attachment
/// policy, model, rate limit, routing rules, candidates and a budget reservation; the provider connection is opened
/// before the client's handshake is accepted, so every refusal (and every fallback) happens as a plain HTTP answer.
/// The session is then relayed, metered and checked against its budgets until either side closes it, and accounted
/// as one usage record.
/// </summary>
public sealed partial class RealtimeRequestHandler(
    RequestStages stages,
    RequestAccounting accounting,
    GatewayCatalog catalog,
    IRouteResolver router,
    BudgetService budgets,
    IRealtimeConnector realtime,
    IRealtimeSessionRegistry sessions,
    IHostApplicationLifetime lifetime,
    GatewayMetrics metrics,
    IOptionsMonitor<GatewayOptions> options,
    TimeProvider time,
    ILogger<GatewayRequestHandler> logger)
{
    // Logs under the GatewayRequestHandler category, as before the handler was split: operators set that category's level
    // (Logging__LogLevel__Ume.LlmGateway.Gateway.Requests.GatewayRequestHandler, docs/runbook.md) to see every request.

    /// <summary>Client headers passed on to the provider: the protocol version of beta Realtime clients.</summary>
    private static readonly string[] RealtimeHeaders = ["OpenAI-Beta"];

    public async Task HandleAsync(HttpContext http, GatewayEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var state = new RequestState(RequestIdMiddleware.Get(http), endpoint, time.GetTimestamp()) { Streamed = true };
        var now = time.GetUtcNow();
        var settings = options.CurrentValue.Realtime;

        if (await stages.AuthenticateAsync(http, endpoint, now, ct) is not { } authenticated)
        {
            return;
        }

        state.Authenticated(http.Request, authenticated);
        // With x-ume-on-behalf-of, the request is made as the assigned key from here on (docs/integrations.md).
        var key = authenticated.Key;
        if (RequestStages.ReadMaxResidency(http.Request, authenticated, out var ceiling) is { } ceilingError)
        {
            await stages.RejectAsync(http, state, ceilingError, RequestOutcome.Rejected);
            return;
        }

        state.MaxResidency = ceiling;
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await stages.RejectAsync(http, state, GatewayError.WebSocketRequired, RequestOutcome.Rejected);
            return;
        }

        var model = http.Request.Query["model"].ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            await stages.RejectAsync(http, state, GatewayError.SessionModelMissing, RequestOutcome.Rejected);
            return;
        }

        state.RequestedModel = ModelNames.Truncate(model);
        if (AttachmentScanner.Disallowed(key.AttachmentPolicy, AttachmentKinds.Audio) is var refused and not AttachmentKinds.None)
        {
            await stages.RejectAsync(http, state, GatewayError.SessionAudioNotAllowed(refused), RequestOutcome.Rejected);
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        var resolution = router.ResolveModel(snapshot, key, endpoint, model);
        if (resolution.Rejection is { } modelRejection)
        {
            await stages.RejectAsync(http, state, modelRejection, RequestOutcome.Rejected);
            return;
        }

        var circuitPrefetch = resolution.Model is null ? null : router.PrefetchCircuits(resolution.Model, ct);
        if (!await stages.AcquireRateLimitAsync(http, state, key, ct, inFlight: false)) // sessions have their own limit
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
            : await stages.ApplyRoutingAsync(http, state, snapshot, key, model, QueryParameters(query), 0, false, ct);

        var route = await stages.SelectRouteAsync(http, state, snapshot, key, model, resolution.Model, routing, null, false, circuitPrefetch, ct);
        if (route is not { } selected)
        {
            return;
        }

        // A slot per open session, shared across instances; it expires by itself if this instance dies.
        if (!await TryOpenSessionAsync(key.Id, state.RequestId, settings.MaxSessionsPerKey, settings.SessionSlotLifetime, ct))
        {
            await stages.RejectAsync(http, state, GatewayError.TooManySessions(settings.MaxSessionsPerKey), RequestOutcome.RateLimited);
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
        var estimate = candidates.Max(t => CostCalculator.Calculate(block, t.ModelDeployment!.PriceAt(now)));
        var applicable = BudgetService.ApplicableBudgets(snapshot, key);
        var reservation = await stages.TryReserveAsync(http, state, snapshot, applicable, estimate, ct, 0,
            [.. candidates.Select(t => t.ModelDeployment!.ProviderAccount!.Residency)]);
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
                if (!stages.TryGetCredential(snapshot, provider, out var credential))
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
                    await accounting.AccountAsync(state, target, default, 499, RequestOutcome.ClientCancelled, null, snapshot);
                    return;
                }

                if (failure is null)
                {
                    (upstream, deployment) = (socket, target);
                    break;
                }

                if (!failure.CanFallBack)
                {
                    RequestStages.SetRoutingHeaders(http, target, i);
                    await GatewayErrors.WriteAsync(http, endpoint, GatewayError.SessionRejectedByProvider(failure.StatusCode), options.CurrentValue.ErrorDocsUrl);
                    await accounting.AccountAsync(state, target, default, failure.StatusCode, RequestOutcome.ProviderError, GatewayErrorCodes.ProviderRejected, snapshot);
                    return;
                }

                await stages.RecordFailureAsync(state, target, failure);
                lastFailure = failure;
            }

            if (upstream is null || deployment is null)
            {
                await stages.RejectAllFailedAsync(http, state, candidates.Count, route.ModelName, lastFailure);
                return;
            }

            using var upstreamLifetime = upstream;
            var account = deployment.ProviderAccount!;
            RequestStages.SetRoutingHeaders(http, deployment, state.Fallbacks);
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
                await accounting.AccountAsync(state, deployment, default, 499, RequestOutcome.ClientCancelled, null, snapshot);
                return;
            }

            using var clientLifetime = client;
            await stages.RecordSuccessAsync(state, account);

            // An input transcription model the client picks must be one the key may use, on this provider, and eligible.
            var transcription = new RoutingConstraints(endpoint, RouteSelector.EffectiveResidencies(key.AllowedResidencies, null, state.MaxResidency), null, key.AllowedProviders);
            var sessionPolicy = policy = new RealtimeEventPolicy(deployment, name => SameProviderTranscription(snapshot, key, account, transcription, name),
                key.PiiPolicy, account.Residency == DataResidency.OnPrem, key.AllowProviderTools);
            var sessionMeter = meter = new RealtimeMeter();
            var sessionPrice = price = deployment.PriceAt(now);
            var started = time.GetTimestamp();
            var reservedUsd = estimate;
            var blockUsd = CostCalculator.Calculate(block, sessionPrice);
            var intervalUsd = CostCalculator.Calculate(CostCalculator.EstimateRealtime(settings.BudgetCheckSeconds), sessionPrice);

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
                    return new RealtimeStop(RealtimeEndReason.TimeLimit, GatewayError.SessionTimeLimit(settings.MaxSessionMinutes), WebSocketCloseStatus.NormalClosure);
                }

                if (reservation.Checks.Count == 0 || SessionCostUsd(sessionMeter, sessionPolicy, sessionPrice) + intervalUsd <= reservedUsd)
                {
                    return null;
                }

                try
                {
                    var more = await budgets.ReserveAsync(applicable, blockUsd, 0, [account.Residency], token);
                    if (!more.Allowed)
                    {
                        return new RealtimeStop(RealtimeEndReason.BudgetExceeded,
                            GatewayError.BudgetExceeded(more.Exceeded!.Budget, "The session is ending."), WebSocketCloseStatus.PolicyViolation);
                    }

                    extra.Add(more);
                    reservedUsd += blockUsd;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The shared store is briefly unavailable: keep the session and try again at the next check.
                    RequestAccounting.LogAccountingFailed(logger, ex, state.RequestId);
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
            await accounting.ReleaseAsync(extra, state.RequestId);
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

            await accounting.AccountAsync(state, deployment, SessionUsage(sessionMeter), status, outcome, errorCode, snapshot,
                SessionCostUsd(sessionMeter, sessionPolicy, sessionPrice));
        }
        catch (Exception ex) when (!state.Accounted)
        {
            // Bill what the session used before it broke, then let the shared handling answer or abort.
            await accounting.ReleaseAsync(extra, state.RequestId);
            var used = meter is null ? default : SessionUsage(meter);
            decimal? costUsd = meter is null || policy is null ? null : SessionCostUsd(meter, policy, price);
            if (!await stages.AccountUnexpectedAsync(http, state, snapshot, ex, used, costUsd))
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

    private static JsonObject QueryParameters(List<KeyValuePair<string, string>> query)
    {
        var parameters = new JsonObject();
        foreach (var (name, value) in query)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    /// <summary>Cost so far in USD: the session model at its price, plus a separately chosen input transcription model at its own.</summary>
    private decimal SessionCostUsd(RealtimeMeter meter, RealtimeEventPolicy policy, ModelPrice? price)
    {
        var (session, transcription) = meter.Snapshot();
        var cost = CostCalculator.Calculate(session, price);
        if (policy.TranscriptionDeployment is { } other)
        {
            cost += CostCalculator.Calculate(transcription, other.PriceAt(time.GetUtcNow()));
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
        if (resolved is not { Kind: ModelKind.Transcription } || !resolved.IsAllowedFor(key))
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
