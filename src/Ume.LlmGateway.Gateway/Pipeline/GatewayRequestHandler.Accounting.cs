using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Accounting: every request that got past authentication ends in exactly one usage record.</summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>
    /// How long accounting may wait for the shared stores, and then again for room in the usage queue, after the client
    /// is done. A store that does not answer in time is logged and the usage record still queued; a record that cannot be
    /// queued in time is dropped and logged (the queue only stays full while the database is unreachable).
    /// </summary>
    internal TimeSpan AccountingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reconciles the budget reservation, records rate-limit tokens and queues the usage record. The cost is calculated
    /// from <paramref name="usage"/> at the deployment's price unless given (live sessions with two priced models).
    /// Runs once per request: later calls are ignored.
    /// </summary>
    private async Task AccountAsync(RequestState state, ModelDeployment? deployment, TokenUsage usage, int status, RequestOutcome outcome, string? errorCode, CatalogSnapshot? snapshot, Cost? knownCost = null)
    {
        if (state.Accounted)
        {
            return;
        }

        state.Accounted = true;
        var key = state.Key!;
        var now = time.GetUtcNow();
        var cost = knownCost ?? (deployment is not null && snapshot is not null
            ? CostCalculator.Calculate(usage, deployment.PriceAt(now), snapshot.SekPerUsd)
            : default);

        // Accounting must complete even if the client disconnected, so it never uses the request's token.
        IReadOnlyList<AlertCandidate> alerts = [];
        using (var stores = new CancellationTokenSource(AccountingTimeout))
        {
            try
            {
                // Issued together so a shared store pipelines them into a single round trip.
                var commit = state.Reservation is { } reservation ? budgets.CommitAsync(reservation, cost.Sek, stores.Token) : Task.FromResult(alerts);
                var tokens = usage.Total > 0 ? rateLimiter.RecordTokensAsync(key.Id, usage.Total, stores.Token) : Task.CompletedTask;
                await Task.WhenAll(commit, tokens);
                alerts = await commit;
            }
            catch (OperationCanceledException)
            {
                // Only our own timeout is passed in: the store did not answer. The usage record is still written.
                LogAccountingTimedOut(logger, state.RequestId);
            }
            catch (Exception ex)
            {
                LogAccountingFailed(logger, ex, state.RequestId);
            }
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
        if (state.PiiAction is { } piiAction && _security.IsEnabled(LogLevel.Information))
        {
            SecurityEvents.PiiAction(_security, PiiActionName(piiAction), state.RequestId, state.Endpoint.ToString(), state.PiiCategories,
                key.Id, key.Prefix, key.TeamId, record.DepartmentId);
        }

        if ((errorCode is GatewayErrorCodes.AttachmentNotAllowed or GatewayErrorCodes.ModelNotAllowed) && _security.IsEnabled(LogLevel.Information))
        {
            SecurityEvents.RequestRefused(_security, state.RequestId, state.Endpoint.ToString(), errorCode, key.Id, key.Prefix, key.TeamId, record.DepartmentId);
        }

        var work = new UsageWork(record, alerts);
        if (usageWriter.TryEnqueue(work))
        {
            return;
        }

        // The queue is full (database unreachable): wait for room, but not forever.
        using var queue = new CancellationTokenSource(AccountingTimeout);
        try
        {
            await usageWriter.EnqueueAsync(work, queue.Token);
        }
        catch (OperationCanceledException)
        {
            LogUsageDropped(logger, state.RequestId);
        }
    }

    /// <summary>The <c>Action</c> attribute of the <c>gateway.pii.action</c> security event.</summary>
    private static string PiiActionName(PiiPolicy policy) => policy switch
    {
        PiiPolicy.Block => "blocked",
        PiiPolicy.Redact => "redacted",
        PiiPolicy.RerouteToOnPrem => "rerouted_onprem",
        _ => "detected",
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

        /// <summary>The deployment of the current (or last) attempt.</summary>
        public ModelDeployment? Deployment { get; set; }

        /// <summary>Set by the one <see cref="AccountAsync"/> call that counts.</summary>
        public bool Accounted { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {RequestId} key {KeyPrefix} model {Model} provider {Provider} status {Status} in {LatencyMs} ms (fallbacks {Fallbacks})")]
    private static partial void LogCompleted(ILogger logger, string requestId, string keyPrefix, string model, string provider, int status, int latencyMs, int fallbacks);

    [LoggerMessage(Level = LogLevel.Error, Message = "Accounting failed for request {RequestId}")]
    private static partial void LogAccountingFailed(ILogger logger, Exception ex, string requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Accounting for request {RequestId} timed out in the shared store; budget and token counters may be off until the period ends, the usage record is still written")]
    private static partial void LogAccountingTimedOut(ILogger logger, string requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Usage record for request {RequestId} could not be queued")]
    private static partial void LogUsageDropped(ILogger logger, string requestId);
}
