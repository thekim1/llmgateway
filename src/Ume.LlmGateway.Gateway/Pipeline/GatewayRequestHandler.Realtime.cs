using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline.Realtime;
using Ume.LlmGateway.Infrastructure;
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

        state.RequestedModel = model.Length > 200 ? model[..200] : model;
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
            await RejectAsync(http, state, modelRejection.StatusCode, modelRejection.Code, modelRejection.Message, RequestOutcome.Rejected);
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
        var routing = new RoutingDecision([], [], false);
        if (snapshot.Rules.Count > 0)
        {
            var parameters = new JsonObject();
            foreach (var (name, value) in query)
            {
                parameters[name] = value;
            }

            routing = snapshot.Rules.Evaluate(await BuildRoutingContextAsync(http, snapshot, key, endpoint, model, parameters, 0, false, ct), Random.Shared);
        }

        if (routing.Applied.Count > 0)
        {
            state.Rule = routing.Applied[^1];
            http.Response.Headers["x-ume-rule"] = string.Join(',', routing.Applied.Select(a => a.RuleId.ToString("D")));
        }

        var plan = router.Plan(snapshot, key, endpoint, model, resolution.Model, routing);
        if (plan.Rejection is { } planRejection)
        {
            await RejectAsync(http, state, planRejection.StatusCode, planRejection.Code, planRejection.Message, RequestOutcome.Rejected);
            return;
        }

        var selection = await router.SelectCandidatesAsync(plan.Models, key, endpoint, null, false, ct, circuitPrefetch);
        if (selection.Rejection is { } selectionRejection)
        {
            await RejectAsync(http, state, selectionRejection.StatusCode, selectionRejection.Code, selectionRejection.Message, RequestOutcome.Rejected);
            return;
        }

        // A slot per open session, shared across instances; it expires by itself if this instance dies.
        var slotLifetime = TimeSpan.FromSeconds(settings.BudgetCheckSeconds * 3 + 30);
        if (!await TryOpenSessionAsync(key.Id, state.RequestId, settings.MaxSessionsPerKey, slotLifetime, ct))
        {
            await RejectAsync(http, state, 429, GatewayErrorCodes.RateLimited,
                $"Nyckeln har redan {settings.MaxSessionsPerKey} öppna realtidssessioner. Stäng en session och försök igen.", RequestOutcome.RateLimited);
            return;
        }

        try
        {
            await RunRealtimeAsync(http, state, endpoint, key, snapshot, selection.Candidates, query, plan.Models[0].Name, settings, ct);
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

    private async Task RunRealtimeAsync(HttpContext http, RequestState state, GatewayEndpoint endpoint, VirtualKey key, CatalogSnapshot snapshot,
        IReadOnlyList<RouteTarget> candidates, List<KeyValuePair<string, string>> query, string modelName, RealtimeOptions settings, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Budget: a block of minutes is reserved now and topped up while the session runs.
        var block = CostCalculator.EstimateRealtime(settings.ReserveMinutes * 60m);
        var estimate = candidates.Max(t => CostCalculator.Calculate(block, t.ModelDeployment!.PriceAt(now), snapshot.SekPerUsd).Sek);
        var applicable = BudgetService.ApplicableBudgets(snapshot, key);
        var reservation = await budgets.ReserveAsync(applicable, estimate, ct);
        state.Reservation = reservation;
        if (reservation.RemainingSek is { } remainingBefore)
        {
            http.Response.Headers["x-ume-budget-remaining-sek"] = FormatSek(remainingBefore);
        }

        if (!reservation.Allowed)
        {
            var b = reservation.Exceeded!.Budget;
            await RejectAsync(http, state, 402, GatewayErrorCodes.BudgetExceeded,
                $"Budgeten ({b.LimitSek:0.##} kr per {PeriodName(b.Period)}) är förbrukad. Kontakta budgetansvarig.", RequestOutcome.BudgetExceeded);
            return;
        }

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

            if (!failure.Retryable && !IsProviderConfigError(failure.StatusCode))
            {
                SetRoutingHeaders(http, target, i);
                await GatewayErrors.WriteAsync(http, endpoint, failure.StatusCode, GatewayErrorCodes.ProviderRejected,
                    $"Leverantören avvisade sessionen ({failure.StatusCode}). Kontrollera parametrarna i anslutningsadressen.", options.CurrentValue.DocsUrl);
                await AccountAsync(state, target, default, failure.StatusCode, RequestOutcome.ProviderError, GatewayErrorCodes.ProviderRejected, snapshot);
                return;
            }

            await circuits.RecordFailureAsync(provider.Id, CancellationToken.None);
            LogProviderFailed(logger, state.RequestId, provider.Name, failure.StatusCode, failure.Reason);
            lastFailure = failure;
        }

        if (upstream is null || deployment is null)
        {
            state.Fallbacks = Math.Max(0, candidates.Count - 1);
            http.Response.Headers["x-ume-fallbacks"] = state.Fallbacks.ToString(CultureInfo.InvariantCulture);
            await RejectAsync(http, state, lastFailure?.StatusCode == 504 ? 504 : 502, GatewayErrorCodes.AllProvidersFailed,
                $"Alla {candidates.Count} leverantörer för '{modelName}' misslyckades. Senaste fel: {lastFailure?.Reason ?? "okänt"}.", RequestOutcome.ProviderError);
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
        await circuits.RecordSuccessAsync(account.Id, CancellationToken.None);

        var policy = new RealtimeEventPolicy(deployment, name => SameProviderTranscription(snapshot, key, account, name), key.PiiPolicy,
            account.Residency == DataResidency.OnPrem);
        var meter = new RealtimeMeter();
        var price = deployment.PriceAt(now);
        var started = time.GetTimestamp();
        var extra = new List<BudgetReservation>();
        var reservedSek = estimate;
        var blockSek = CostCalculator.Calculate(block, price, snapshot.SekPerUsd).Sek;
        var intervalSek = CostCalculator.Calculate(CostCalculator.EstimateRealtime(settings.BudgetCheckSeconds), price, snapshot.SekPerUsd).Sek;

        async Task<RealtimeStop?> CheckAsync(CancellationToken token)
        {
            try
            {
                await sessions.RenewAsync(key.Id, state.RequestId, TimeSpan.FromSeconds(settings.BudgetCheckSeconds * 3 + 30), token);
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

            if (reservation.Checks.Count == 0 || SessionCost(meter, policy, price, snapshot).Sek + intervalSek <= reservedSek)
            {
                return null;
            }

            try
            {
                var more = await budgets.ReserveAsync(applicable, blockSek, token);
                if (!more.Allowed)
                {
                    var b = more.Exceeded!.Budget;
                    return new RealtimeStop(RealtimeEndReason.BudgetExceeded, GatewayErrorCodes.BudgetExceeded,
                        $"Budgeten ({b.LimitSek:0.##} kr per {PeriodName(b.Period)}) är förbrukad. Sessionen avslutas.", WebSocketCloseStatus.PolicyViolation);
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
        using (var relay = new RealtimeRelay(client, upstream, policy, meter, settings.MaxMessageBytes, logger))
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
        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var more in extra)
        {
            try
            {
                await budgets.CommitAsync(more, 0, release.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAccountingFailed(logger, ex, state.RequestId);
            }
        }

        var (sessionUsage, transcriptionUsage) = meter.Snapshot();
        state.PiiAction = policy.PiiApplied;
        state.PiiCategories = policy.PiiCategories;
        var (status, outcome, errorCode) = reason switch
        {
            RealtimeEndReason.ClientLost => (499, RequestOutcome.ClientCancelled, (string?)null),
            RealtimeEndReason.ProviderFailed => (502, RequestOutcome.ProviderError, "stream_interrupted"),
            RealtimeEndReason.BudgetExceeded => (402, RequestOutcome.BudgetExceeded, GatewayErrorCodes.BudgetExceeded),
            RealtimeEndReason.TimeLimit => (200, RequestOutcome.Success, "session_time_limit"),
            RealtimeEndReason.Shutdown => (200, RequestOutcome.Success, "gateway_shutting_down"),
            _ => (200, RequestOutcome.Success, null),
        };

        // Audio duration is the session's; the input transcription model only adds its tokens (and its own price).
        var usage = sessionUsage + (transcriptionUsage with { AudioSeconds = 0 });
        await AccountAsync(state, deployment, usage, status, outcome, errorCode, snapshot, SessionCost(meter, policy, price, snapshot));
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
    /// model the key may use, served by the session's provider (the audio is already there).
    /// </summary>
    private static ModelDeployment? SameProviderTranscription(CatalogSnapshot snapshot, VirtualKey key, ProviderAccount provider, string name)
    {
        var resolved = snapshot.Resolve(name);
        if (resolved is not { Kind: ModelKind.Transcription }
            || (key.AllowedModels.Count > 0 && !key.AllowedModels.Contains(resolved.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        return resolved.Targets
            .Select(t => t.ModelDeployment)
            .FirstOrDefault(d => d is { IsEnabled: true } && d.ProviderAccountId == provider.Id);
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
