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

    /// <summary>Counts the request against the key's per-minute limits; when refused, the 429 has been written.</summary>
    private async Task<bool> AcquireRateLimitAsync(HttpContext http, RequestState state, VirtualKey key, CancellationToken ct)
    {
        var rate = await rateLimiter.AcquireAsync(key.Id, key.RequestsPerMinute, key.TokensPerMinute, ct);
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
        var what = rate.Reason == "tokens" ? "token per minut" : "anrop per minut";
        await RejectAsync(http, state, 429, GatewayErrorCodes.RateLimited, $"Gränsen för {what} är nådd för nyckeln. Försök igen om {retryAfter} s.", RequestOutcome.RateLimited);
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
    private async Task<SelectedRoute?> SelectRouteAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key, string model,
        ResolvedModel? requested, RoutingDecision routing, DataResidency? restrictTo, bool stream, CircuitPrefetch? prefetch, CancellationToken ct)
    {
        var plan = router.Plan(snapshot, key, state.Endpoint, model, requested, routing);
        if (plan.Rejection is { } planRejection)
        {
            await RejectAsync(http, state, planRejection);
            return null;
        }

        var selection = await router.SelectCandidatesAsync(plan.Models, key, state.Endpoint, restrictTo, stream, ct, prefetch);
        if (selection.Rejection is { } selectionRejection)
        {
            await RejectAsync(http, state, selectionRejection);
            return null;
        }

        return new SelectedRoute(selection.Candidates, plan.Models[0].Name);
    }

    /// <summary>
    /// Reserves <paramref name="estimateSek"/> against every applicable budget and reports what remains in
    /// <c>x-ume-budget-remaining-sek</c>. Null when a budget is exhausted (the 402 has been written).
    /// </summary>
    private async Task<BudgetReservation?> TryReserveAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, IReadOnlyList<Budget> applicable,
        decimal estimateSek, CancellationToken ct)
    {
        var reservation = await budgets.ReserveAsync(applicable, estimateSek, ct);
        state.Reservation = reservation;
        if (reservation.RemainingSek is { } remainingBefore)
        {
            http.Response.Headers[GatewayHeaders.BudgetRemainingSek] = FormatSek(remainingBefore);
        }

        if (reservation.Allowed)
        {
            return reservation;
        }

        await RejectAsync(http, state, 402, GatewayErrorCodes.BudgetExceeded,
            BudgetExceededMessage(reservation.Exceeded!.Budget, "Kontakta budgetansvarig."), RequestOutcome.BudgetExceeded);
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
    /// Logs a failed attempt the next candidate will take over, and counts transient failures against the provider's
    /// circuit. The circuit store being unavailable does not fail the request.
    /// </summary>
    private async Task RecordFailureAsync(RequestState state, ProviderAccount provider, ProviderFailure failure)
    {
        LogProviderFailed(logger, state.RequestId, provider.Name, failure.StatusCode, failure.Reason);
        if (!failure.CountsAgainstCircuit)
        {
            return;
        }

        try
        {
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

    private Task RejectAsync(HttpContext http, RequestState state, RouteRejection rejection) =>
        RejectAsync(http, state, rejection.StatusCode, rejection.Code, rejection.Message, RequestOutcome.Rejected);

    private async Task RejectAsync(HttpContext http, RequestState state, int status, string code, string message, RequestOutcome outcome)
    {
        await GatewayErrors.WriteAsync(http, state.Endpoint, status, code, message, options.CurrentValue.DocsUrl);
        await AccountAsync(state, null, default, status, outcome, code, null);
    }

    /// <summary>Every candidate failed: 504 when the last one timed out, else 502.</summary>
    private Task RejectAllFailedAsync(HttpContext http, RequestState state, int candidates, string modelName, ProviderFailure? lastFailure)
    {
        state.Fallbacks = Math.Max(0, candidates - 1);
        http.Response.Headers[GatewayHeaders.Fallbacks] = state.Fallbacks.ToString(CultureInfo.InvariantCulture);
        return RejectAsync(http, state, lastFailure?.StatusCode == 504 ? 504 : 502, GatewayErrorCodes.AllProvidersFailed,
            $"Alla {candidates} leverantörer för '{modelName}' misslyckades. Senaste fel: {lastFailure?.Reason ?? "okänt"}.", RequestOutcome.ProviderError);
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

        await GatewayErrors.WriteAsync(http, state.Endpoint, 500, GatewayErrorCodes.InternalError,
            "Ett internt fel uppstod i gatewayen. Försök igen; om felet kvarstår, kontakta förvaltningen och ange request-id.", options.CurrentValue.DocsUrl);
        return true;
    }

    private static string BudgetExceededMessage(Budget budget, string action)
    {
        var scope = budget.Scope switch
        {
            BudgetScope.Department => "Förvaltningens",
            BudgetScope.Team => "Teamets",
            _ => "Nyckelns",
        };
        return $"{scope} budget ({budget.LimitSek:0.##} kr per {PeriodName(budget.Period)}) är förbrukad. {action}";
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

    private static readonly (AttachmentKinds Kind, string Name)[] AttachmentKindNames =
        [(AttachmentKinds.Image, "bilder"), (AttachmentKinds.Document, "dokument"), (AttachmentKinds.Audio, "ljudfiler"), (AttachmentKinds.Video, "videofiler")];

    private static string AttachmentNames(AttachmentKinds kinds) =>
        string.Join(" eller ", AttachmentKindNames.Where(n => kinds.HasFlag(n.Kind)).Select(n => n.Name));

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: provider {Provider} failed with {Status} ({Reason}); trying next")]
    private static partial void LogProviderFailed(ILogger logger, string requestId, string provider, int status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: circuit breaker store unavailable ({ExceptionType}); provider health not recorded")]
    private static partial void LogCircuitStoreFailed(ILogger logger, string exceptionType, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: stream from {Provider} interrupted ({Error})")]
    private static partial void LogStreamBroken(ILogger logger, string requestId, string provider, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Credential for provider {Provider} cannot be decrypted (Data Protection key ring missing?)")]
    private static partial void LogCredentialUnavailable(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Error, Message = "Request {RequestId} failed unexpectedly; its budget reservation was released and the usage recorded")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception ex, string requestId);
}
