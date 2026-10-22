using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Pipeline stages shared by HTTP requests and live sessions. A stage that refuses the request writes the answer and
/// accounts it, and tells the caller to stop (false / null).
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>The models chosen for a request, as an ordered attempt list, and the name failures are reported under.</summary>
    private readonly record struct SelectedRoute(IReadOnlyList<RouteTarget> Candidates, string ModelName);

    private async Task<VirtualKey?> AuthenticateAsync(HttpContext http, GatewayEndpoint endpoint, DateTimeOffset now, CancellationToken ct)
    {
        var docs = options.CurrentValue.ErrorDocsUrl;
        var presented = KeyAuthenticator.ExtractKey(http.Request);
        var address = http.Connection.RemoteIpAddress;
        VirtualKey? key;
        if (authThrottle.IsLimited(address, out var retryAfter))
        {
            // Too many failed authentications from this address: no database lookups for it until the window ends.
            key = keys.FindCached(presented);
            if (key is null)
            {
                http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                await GatewayErrors.WriteAsync(http, endpoint, GatewayError.TooManyAuthFailures(retryAfter), docs);
                return null;
            }
        }
        else
        {
            key = await keys.FindAsync(presented, ct);
        }

        if (key is null)
        {
            authThrottle.RecordFailure(address);
            authFailures.Record(string.IsNullOrEmpty(presented) ? AuthFailureReason.MissingKey : AuthFailureReason.InvalidKey,
                endpoint, null, http.Connection.RemoteIpAddress);
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await GatewayErrors.WriteAsync(http, endpoint, GatewayError.InvalidApiKey, docs);
            return null;
        }

        var (error, reason) = key.GetStatus(now) switch
        {
            KeyStatus.Revoked => (GatewayError.KeyRevoked, AuthFailureReason.KeyRevoked),
            KeyStatus.Expired => (GatewayError.KeyExpired, AuthFailureReason.KeyExpired),
            KeyStatus.Disabled => (GatewayError.KeyDisabled, AuthFailureReason.KeyDisabled),
            _ when key.Team is not { IsActive: true } || key.Team.Department is not { IsActive: true }
                => (GatewayError.KeyOwnerInactive, AuthFailureReason.OwnerInactive),
            _ => (null, default),
        };

        // After the status checks, so a revoked key is reported as revoked from wherever it is used.
        if (error is null && !key.IsAddressAllowed(http.Connection.RemoteIpAddress))
        {
            (error, reason) = (GatewayError.AddressNotAllowed, AuthFailureReason.AddressNotAllowed);
        }

        if (error is not null)
        {
            authFailures.Record(reason, endpoint, key, http.Connection.RemoteIpAddress);
            await GatewayErrors.WriteAsync(http, endpoint, error, docs);
            return null;
        }

        return key;
    }

    /// <summary>
    /// Counts the request against the key's per-minute limits and, with <paramref name="inFlight"/>, takes one of the
    /// key's in-flight slots until the response has completed (or the client went away). When refused, the 429 has been written.
    /// </summary>
    private async Task<bool> AcquireRateLimitAsync(HttpContext http, RequestState state, VirtualKey key, CancellationToken ct, bool inFlight = true)
    {
        var maxConcurrent = inFlight ? key.MaxConcurrentRequests : null;
        // Not the request id: a client may set that, and repeating it would share one slot.
        var lease = maxConcurrent is > 0 ? Guid.NewGuid().ToString("N") : null;
        var rate = await rateLimiter.AcquireAsync(key.Id, key.RequestsPerMinute, key.TokensPerMinute, maxConcurrent, lease, ct);
        if (rate.Allowed && lease is not null)
        {
            var (limiter, keyId) = (rateLimiter, key.Id);
            http.Response.OnCompleted(() =>
            {
                limiter.Release(keyId, lease);
                return Task.CompletedTask;
            });
        }

        if (rate.RequestLimit is { } limit)
        {
            http.Response.Headers[GatewayHeaders.RateLimitLimitRequests] = limit.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers[GatewayHeaders.RateLimitRemainingRequests] = Math.Max(0, rate.RequestsRemaining ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        if (rate.Allowed)
        {
            return true;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(rate.RetryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await RejectAsync(http, state, rate.Reason == "concurrency"
            ? GatewayError.TooManyConcurrentRequests(key.MaxConcurrentRequests ?? 0)
            : GatewayError.RateLimited(rate.Reason == "tokens", retryAfter), RequestOutcome.RateLimited);
        return false;
    }

    /// <summary>
    /// Evaluates the routing rules (when there are any) and reports the rules that applied in <c>x-ume-rule</c>.
    /// <paramref name="parameters"/> holds what conditions may read: the body, or a session's query parameters.
    /// </summary>
    private async Task<RoutingDecision> ApplyRoutingAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key, string model,
        JsonObject parameters, long promptTokens, bool? piiDetected, CancellationToken ct)
    {
        if (snapshot.Rules.Count == 0)
        {
            return RoutingDecision.Empty;
        }

        var routing = snapshot.Rules.Evaluate(
            await BuildRoutingContextAsync(http, snapshot, key, state.Endpoint, model, parameters, promptTokens, piiDetected, ct), Random.Shared);
        if (routing.Applied.Count > 0)
        {
            state.Rule = routing.Applied[^1];
            http.Response.Headers[GatewayHeaders.Rule] = string.Join(',', routing.Applied.Select(a => a.RuleId.ToString("D")));
        }

        return routing;
    }

    /// <summary>Turns the routing decision into models and orders their eligible targets into an attempt list.</summary>
    /// <summary>
    /// What keeps a conversation on one server (so its prompt cache is reused): the <c>x-ume-session</c> header, else
    /// OpenAI's <c>prompt_cache_key</c> body parameter. Null when the client sends neither. Scoped to the key, so two
    /// applications using the same session name do not share a target by accident (they would not share a cache either).
    /// </summary>
    private static string? SessionAffinity(HttpContext http, JsonObject body, VirtualKey key)
    {
        var session = http.Request.Headers[GatewayHeaders.Session].ToString();
        if (string.IsNullOrEmpty(session) && body["prompt_cache_key"] is JsonValue v && v.TryGetValue<string>(out var cacheKey))
        {
            session = cacheKey;
        }

        return string.IsNullOrEmpty(session) || session.Length > 200 ? null : $"{key.Id:N}:{session}";
    }

    /// <summary>
    /// The estimate may exceed a model's context window by this much before the model is skipped: it is counted from the
    /// body's bytes (JSON syntax included), so it runs high for prompts in English and low for Swedish.
    /// </summary>
    private const double ContextWindowMargin = 1.1;

    /// <summary>
    /// The candidates in attempt order, or null when the request was refused. With <paramref name="estimatedInputTokens"/>,
    /// models whose context window the prompt clearly exceeds are skipped (a route's larger fallback model takes it);
    /// when none is left the request is refused with <c>context_length_exceeded</c> instead of failing at the provider.
    /// </summary>
    private async Task<SelectedRoute?> SelectRouteAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key, string model,
        ResolvedModel? requested, RoutingDecision routing, DataResidency? restrictTo, bool stream, CircuitPrefetch? prefetch, CancellationToken ct,
        long estimatedInputTokens = 0, string? affinity = null)
    {
        var plan = router.Plan(snapshot, key, state.Endpoint, model, requested, routing);
        if (plan.Rejection is { } planRejection)
        {
            await RejectAsync(http, state, planRejection, RequestOutcome.Rejected);
            return null;
        }

        var selection = await router.SelectCandidatesAsync(plan.Models, key, state.Endpoint, restrictTo, stream, ct, prefetch, affinity);
        if (selection.Rejection is { } selectionRejection)
        {
            await RejectAsync(http, state, selectionRejection, RequestOutcome.Rejected);
            return null;
        }

        var candidates = selection.Candidates;
        if (estimatedInputTokens > 0 && candidates.Any(t => !Fits(t, estimatedInputTokens)))
        {
            var fitting = candidates.Where(t => Fits(t, estimatedInputTokens)).ToList();
            if (fitting.Count == 0)
            {
                var largest = candidates.Max(t => t.ModelDeployment!.ContextWindow ?? 0);
                await RejectAsync(http, state, GatewayError.ContextTooLong(plan.Models[0].Name, estimatedInputTokens, largest), RequestOutcome.Rejected);
                return null;
            }

            candidates = fitting;
        }

        return new SelectedRoute(candidates, plan.Models[0].Name);

        static bool Fits(RouteTarget target, long tokens) =>
            target.ModelDeployment!.ContextWindow is not { } window || tokens <= window * ContextWindowMargin;
    }

    /// <summary>
    /// Reserves <paramref name="estimateUsd"/> against every applicable cost budget and <paramref name="estimateTokens"/>
    /// against the token budgets that count one of <paramref name="residencies"/>, and reports what remains in
    /// <c>x-ume-budget-remaining-usd</c>. Null when a budget is exhausted (the 402 has been written).
    /// </summary>
    private async Task<BudgetReservation?> TryReserveAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, IReadOnlyList<Budget> applicable,
        decimal estimateUsd, CancellationToken ct, long estimateTokens = 0, IReadOnlyCollection<DataResidency>? residencies = null)
    {
        var reservation = await budgets.ReserveAsync(applicable, estimateUsd, estimateTokens, residencies, ct);
        state.Reservation = reservation;
        if (reservation.RemainingUsd is { } remainingBefore)
        {
            SetBudgetRemainingHeaders(http, remainingBefore, snapshot);
        }

        if (reservation.Allowed)
        {
            return reservation;
        }

        await RejectAsync(http, state, GatewayError.BudgetExceeded(reservation.Exceeded!.Budget, "Contact the budget owner."), RequestOutcome.BudgetExceeded);
        return null;
    }

    /// <summary>The provider's decrypted credential (cached per catalogue snapshot). False when it cannot be decrypted.</summary>
    private bool TryGetCredential(CatalogSnapshot snapshot, ProviderAccount provider, out string? credential)
    {
        try
        {
            credential = snapshot.Credentials.Get(provider, credentials);
            return true;
        }
        catch (CryptographicException)
        {
            LogCredentialUnavailable(logger, provider.Name);
            credential = null;
            return false;
        }
    }

    /// <summary>
    /// Logs a failed attempt the next candidate will take over. A transient failure with <c>Retry-After</c> puts the
    /// deployment on cooldown for that long (capped by <see cref="Infrastructure.Stores.CircuitBreakerOptions.MaxRetryAfter"/>): the provider
    /// said when to come back, so it is a quota rather than a health problem and does not count against the provider's
    /// circuit; other transient failures do. The circuit store being unavailable does not fail the request.
    /// </summary>
    private async Task RecordFailureAsync(RequestState state, ModelDeployment deployment, ProviderFailure failure)
    {
        var provider = deployment.ProviderAccount!;
        LogProviderFailed(logger, state.RequestId, provider.Name, failure.StatusCode, failure.Reason);
        if (!failure.CountsAgainstCircuit)
        {
            return;
        }

        try
        {
            var maxCooldown = circuitOptions.Value.MaxRetryAfter;
            if (failure.RetryAfter is { } retryAfter && maxCooldown > TimeSpan.Zero)
            {
                var cooldown = retryAfter < maxCooldown ? retryAfter : maxCooldown;
                LogDeploymentCooldown(logger, state.RequestId, provider.Name, deployment.UpstreamModel, (long)cooldown.TotalMilliseconds);
                await circuits.CoolDownAsync(deployment.Id, cooldown, CancellationToken.None);
                return;
            }

            await circuits.RecordFailureAsync(provider.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCircuitStoreFailed(logger, ex.GetType().Name, state.RequestId);
        }
    }

    private async Task RecordSuccessAsync(RequestState state, ProviderAccount provider)
    {
        try
        {
            await circuits.RecordSuccessAsync(provider.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCircuitStoreFailed(logger, ex.GetType().Name, state.RequestId);
        }
    }

    /// <summary>Answers with <paramref name="error"/> and accounts the refused request.</summary>
    private async Task RejectAsync(HttpContext http, RequestState state, GatewayError error, RequestOutcome outcome)
    {
        await GatewayErrors.WriteAsync(http, state.Endpoint, error, options.CurrentValue.ErrorDocsUrl);
        await AccountAsync(state, null, default, error.StatusCode, outcome, error.Code, null);
    }

    /// <summary>
    /// Every attempt failed (<paramref name="candidates"/> counts retries too): 504 when the last one timed out, else 502. The usage record names the last provider tried
    /// (nothing is charged), so a provider that is down shows up in the provider statistics instead of only in its circuit.
    /// </summary>
    private async Task RejectAllFailedAsync(HttpContext http, RequestState state, int candidates, string modelName, ProviderFailure? lastFailure)
    {
        state.Fallbacks = Math.Max(0, candidates - 1);
        http.Response.Headers[GatewayHeaders.Fallbacks] = state.Fallbacks.ToString(CultureInfo.InvariantCulture);
        var error = GatewayError.AllProvidersFailed(lastFailure?.StatusCode == 504, candidates, modelName, lastFailure?.Reason);
        await GatewayErrors.WriteAsync(http, state.Endpoint, error, options.CurrentValue.ErrorDocsUrl);
        await AccountAsync(state, state.Deployment, default, error.StatusCode, RequestOutcome.ProviderError, error.Code, null);
    }

    /// <summary>
    /// An exception escaped after the budget reservation: account it (releasing the reservation and recording the usage)
    /// and answer 500 if nothing was sent yet. Returns false when the exception should still propagate (the answer had
    /// started, so the connection has to be aborted).
    /// </summary>
    private async Task<bool> AccountUnexpectedAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, Exception ex,
        TokenUsage usage = default, Cost? cost = null)
    {
        if (ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
        {
            await AccountAsync(state, state.Deployment, usage, 499, RequestOutcome.ClientCancelled, null, snapshot, cost);
            return true;
        }

        LogUnexpectedFailure(logger, ex, state.RequestId);
        await AccountAsync(state, state.Deployment, usage, 500, RequestOutcome.ProviderError, GatewayErrorCodes.InternalError, snapshot, cost);
        if (http.Response.HasStarted)
        {
            return false;
        }

        await GatewayErrors.WriteAsync(http, state.Endpoint, GatewayError.InternalError, options.CurrentValue.ErrorDocsUrl);
        return true;
    }

    private static void SetRoutingHeaders(HttpContext http, ModelDeployment deployment, int fallbacks)
    {
        http.Response.Headers[GatewayHeaders.Provider] = deployment.ProviderAccount!.Name;
        http.Response.Headers[GatewayHeaders.Model] = deployment.Name;
        http.Response.Headers[GatewayHeaders.Residency] = deployment.ProviderAccount.Residency.ToString();
        http.Response.Headers[GatewayHeaders.Fallbacks] = fallbacks.ToString(CultureInfo.InvariantCulture);
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
            Endpoint = GatewayEndpoints.Info(endpoint).RoutingName,
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

    /// <summary>The remaining budget in USD, and in SEK at the snapshot's rate for clients that still read the SEK header.</summary>
    private static void SetBudgetRemainingHeaders(HttpContext http, decimal remainingUsd, CatalogSnapshot snapshot)
    {
        http.Response.Headers[GatewayHeaders.BudgetRemainingUsd] = remainingUsd.ToString("0.00", CultureInfo.InvariantCulture);
        http.Response.Headers[GatewayHeaders.BudgetRemainingSek] = (remainingUsd * snapshot.SekPerUsd).ToString("0.00", CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: provider {Provider} failed with {Status} ({Reason}); trying next")]
    private static partial void LogProviderFailed(ILogger logger, string requestId, string provider, int status, string reason);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Retry jitter, not a security decision.")]
    private static TimeSpan RetryDelay(RetryOptions retry, int retryIndex) =>
        TimeSpan.FromMilliseconds(retry.BaseDelayMs * (1 << retryIndex) * (0.5 + Random.Shared.NextDouble()));

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {RequestId}: retrying provider {Provider} in {DelayMs} ms")]
    private static partial void LogRetrying(ILogger logger, string requestId, string provider, long delayMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: provider {Provider} asked to retry {UpstreamModel} after {CooldownMs} ms; it goes last until then")]
    private static partial void LogDeploymentCooldown(ILogger logger, string requestId, string provider, string upstreamModel, long cooldownMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: circuit breaker store unavailable ({ExceptionType}); provider health not recorded")]
    private static partial void LogCircuitStoreFailed(ILogger logger, string exceptionType, string requestId);

    /// <summary>One stream's gap figures as metrics, and a log line when the provider stalled (so a slow request can be found by id).</summary>
    private void RecordStreamGaps(RequestState state, ModelDeployment deployment, in StreamGaps gaps)
    {
        if (gaps.Events < 2)
        {
            return;
        }

        var provider = deployment.ProviderAccount!.Name;
        metrics.StreamGaps(provider, state.Endpoint, gaps.MaxUpstream.TotalMilliseconds, gaps.MaxWrite.TotalMilliseconds, gaps.Stalls);
        if (gaps.Stalls > 0)
        {
            LogStreamStalled(logger, state.RequestId, provider, gaps.Stalls, (long)gaps.MaxUpstream.TotalMilliseconds, (long)gaps.MaxWrite.TotalMilliseconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {RequestId}: stream from {Provider} stalled {Stalls} time(s); longest wait for the provider {MaxUpstreamGapMs} ms, longest client write {MaxWriteMs} ms")]
    private static partial void LogStreamStalled(ILogger logger, string requestId, string provider, int stalls, long maxUpstreamGapMs, long maxWriteMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: stream from {Provider} interrupted ({Error})")]
    private static partial void LogStreamBroken(ILogger logger, string requestId, string provider, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Credential for provider {Provider} cannot be decrypted (Data Protection key ring missing?)")]
    private static partial void LogCredentialUnavailable(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Error, Message = "Request {RequestId} failed unexpectedly; its budget reservation was released and the usage recorded")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception ex, string requestId);
}
