using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

// Response bodies of the admin API. The admin UI's types (src/admin-ui/src/api/types.ts) mirror them: property names
// are serialised in camelCase and enums by name, so renaming a property here is a breaking change for the UI.

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);

// ---- organisation and keys -----------------------------------------------------------------------------

public sealed record DepartmentDto(Guid Id, string Name, string CostCenterCode, bool IsActive, int TeamCount, DateTimeOffset CreatedAt)
{
    public static DepartmentDto Of(Department d) => new(d.Id, d.Name, d.CostCenterCode, d.IsActive, d.Teams.Count, d.CreatedAt);
}

public sealed record TeamDto(Guid Id, Guid DepartmentId, string? DepartmentName, string Name, string? Description, bool IsActive, int KeyCount, DateTimeOffset CreatedAt,
    RequestPolicy? RequestPolicy = null)
{
    public static TeamDto Of(Team t) => new(t.Id, t.DepartmentId, t.Department?.Name, t.Name, t.Description, t.IsActive, t.VirtualKeys.Count, t.CreatedAt, t.RequestPolicy);
}

public sealed record KeyDto(
    Guid Id, Guid TeamId, string? TeamName, Guid? DepartmentId, string? DepartmentName, string Name, string? Description, string Prefix,
    KeyStatus Status, bool IsEnabled, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? GraceUntil, DateTimeOffset? LastUsedAt, IReadOnlyList<string> AllowedModels, IReadOnlyList<DataResidency> AllowedResidencies,
    IReadOnlyList<string> AllowedProviders, PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, int? RequestsPerMinute, int? TokensPerMinute,
    Guid? RotatedToKeyId, bool CanReveal, IReadOnlyList<string> AllowedIpRanges, int? MaxConcurrentRequests = null, RequestPolicy? RequestPolicy = null)
{
    public static KeyDto Of(VirtualKey k, DateTimeOffset now) => new(
        k.Id, k.TeamId, k.Team?.Name, k.Team?.DepartmentId, k.Team?.Department?.Name, k.Name, k.Description, k.Prefix,
        k.GetStatus(now), k.IsEnabled, k.CreatedAt, k.CreatedBy, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.LastUsedAt,
        k.AllowedModels, k.AllowedResidencies, k.AllowedProviders, k.PiiPolicy, k.AttachmentPolicy, k.RequestsPerMinute, k.TokensPerMinute,
        k.RotatedToKeyId, k.EncryptedSecret is not null, k.AllowedIpRanges, k.MaxConcurrentRequests, k.RequestPolicy);
}

/// <summary>A new key with its secret, which is shown this once.</summary>
public sealed record KeyCreatedDto(KeyDto Key, string Secret);

public sealed record KeyRotatedDto(KeyDto Key, string Secret, KeyDto PreviousKey);

public sealed record KeySecretDto(string Secret);

// ---- Data API access -------------------------------------------------------------------------------------

/// <summary>Where integrations reach the Data API (null when not configured) and the permissions a key can hold.</summary>
public sealed record DataAccessDto(string? DataApiUrl, IReadOnlyList<string> Permissions);

public sealed record DataApiKeyDto(
    Guid Id, string Name, string? Description, string Prefix, IReadOnlyList<string> Permissions, KeyStatus Status,
    DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt)
{
    public static DataApiKeyDto Of(DataApiKey k, DateTimeOffset now) => new(
        k.Id, k.Name, k.Description, k.Prefix, k.Permissions, k.GetStatus(now), k.CreatedAt, k.CreatedBy, k.ExpiresAt, k.RevokedAt, k.LastUsedAt);
}

/// <summary>A new Data API key with its secret, which is shown this once and never stored.</summary>
public sealed record DataApiKeyCreatedDto(DataApiKeyDto Key, string Secret);

// ---- providers, models, routes ---------------------------------------------------------------------------

public sealed record ProviderDto(
    Guid Id, string Name, string? DisplayName, ProviderType Type, string BaseUrl, ProviderAuthMode AuthMode, bool HasCredential,
    DataResidency Residency, ProviderCapabilities[] Capabilities, bool IsEnabled, bool IsDrained, int TimeoutSeconds, DateTimeOffset CreatedAt,
    int DeploymentCount)
{
    public static ProviderDto Of(ProviderAccount p) => new(
        p.Id, p.Name, p.DisplayName, p.Type, p.BaseUrl, p.AuthMode, !string.IsNullOrEmpty(p.EncryptedCredential), p.Residency,
        ProviderCatalogService.Capabilities(p.Capabilities), p.IsEnabled, p.IsDrained, p.TimeoutSeconds, p.CreatedAt, p.Deployments.Count);
}

public sealed record PriceDto(
    decimal InputPerMillionUsd, decimal CachedInputPerMillionUsd, decimal OutputPerMillionUsd, decimal AudioPerMinuteUsd,
    decimal AudioInputPerMillionUsd, decimal AudioOutputPerMillionUsd, DateTimeOffset EffectiveFrom, decimal PerImageUsd = 0,
    decimal? CacheWriteInputPerMillionUsd = null)
{
    public static PriceDto Of(ModelPrice p) => new(
        p.InputPerMillionUsd, p.CachedInputPerMillionUsd, p.OutputPerMillionUsd, p.AudioPerMinuteUsd, p.AudioInputPerMillionUsd,
        p.AudioOutputPerMillionUsd, p.EffectiveFrom, p.PerImageUsd, p.CacheWriteInputPerMillionUsd);
}

public sealed record ModelDto(
    Guid Id, Guid ProviderId, string? ProviderName, DataResidency? Residency, string Name, string UpstreamModel, ModelKind Kind,
    ParameterProfile ParameterProfile, int? ContextWindow, bool IsEnabled, string[] Features, PriceDto? CurrentPrice, string PublicName)
{
    /// <summary>The model; <c>publicName</c> is what clients call it: its name, or provider/name when another provider has the same name.</summary>
    public static ModelDto Of(ModelDeployment m, DateTimeOffset now, string publicName) => new(
        m.Id, m.ProviderAccountId, m.ProviderAccount?.Name, m.ProviderAccount?.Residency, m.Name, m.UpstreamModel, m.Kind,
        m.ParameterProfile, m.ContextWindow, m.IsEnabled, m.Features, m.PriceAt(now) is { } p ? PriceDto.Of(p) : null, publicName);
}

public sealed record RouteDto(Guid Id, string Name, string? Description, ModelKind Kind, bool IsEnabled, RouteTargetDto[] Targets)
{
    public static RouteDto Of(RouteAlias r) => new(
        r.Id, r.Name, r.Description, r.Kind, r.IsEnabled,
        [.. r.Targets.OrderBy(t => t.Priority).Select(t => new RouteTargetDto(
            t.ModelDeploymentId, t.ModelDeployment?.Name, t.ModelDeployment?.ProviderAccount?.Name, t.ModelDeployment?.ProviderAccount?.Residency,
            t.Priority, t.Weight))]);
}

public sealed record RouteTargetDto(Guid ModelId, string? ModelName, string? ProviderName, DataResidency? Residency, int Priority, int Weight);

public sealed record ExchangeRateDto(string Currency, decimal SekPerUnit, DateTimeOffset EffectiveFrom)
{
    public static ExchangeRateDto Of(ExchangeRate r) => new(r.Currency, r.SekPerUnit, r.EffectiveFrom);
}

// ---- budgets ---------------------------------------------------------------------------------------------

public sealed record BudgetDto(
    Guid Id, BudgetScope Scope, Guid ScopeId, string ScopeName, decimal LimitUsd, BudgetPeriod Period, IReadOnlyList<int> AlertThresholds,
    bool IsActive, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, decimal SpentUsd, decimal PercentUsed);

public sealed record AlertDto(
    Guid Id, Guid BudgetId, BudgetScope Scope, string ScopeName, int ThresholdPercent, decimal SpentUsd, decimal LimitUsd,
    DateTimeOffset PeriodStart, DateTimeOffset Timestamp, bool Acknowledged);

// ---- routing rules ---------------------------------------------------------------------------------------

/// <summary>
/// <c>ScopeIds</c>/<c>Owners</c>: the keys, teams or departments a non-global rule is shared by (an owner's name is null
/// when it no longer exists). <c>IsOrphaned</c>: none of them exists any more. Such a rule never applies (and cannot be
/// enabled) until it is reassigned. <c>IsTemplate</c>: a shipped example, disabled until adopted. <c>MissingModels</c>:
/// targets or fallbacks that do not exist; the rule cannot be enabled until they do. <c>ValidationErrors</c> lists why
/// the gateway ignores an enabled rule.
/// </summary>
public sealed record RuleView(
    Guid Id, string Name, string? Description, bool IsEnabled, bool IsTemplate, int Priority, string Scope, IReadOnlyList<Guid> ScopeIds,
    IReadOnlyList<RuleOwnerView> Owners, bool IsOrphaned, string Condition, bool Chain, IReadOnlyList<RuleTargetView> Targets, IReadOnlyList<string> Fallbacks,
    IReadOnlyList<string> MissingModels, IReadOnlyList<RuleProblem> ValidationErrors, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RuleOwnerView(Guid Id, string? Name);

public sealed record RuleTargetView(string Model, int Weight);

public sealed record RuleProblem(string Message, int? Position, int? Length);

public sealed record ConditionCheckResult(bool Valid, IReadOnlyList<ConditionError> Errors, IReadOnlyList<string> Variables, IReadOnlyList<VariableView> Available);

public sealed record ConditionError(int Position, int Length, string Message)
{
    public static ConditionError Of(ExpressionError e) => new(e.Position, e.Length, e.Message);
}

public sealed record VariableView(string Name, string Type);

public sealed record RuleTestResult(
    bool Matched, string? PrimaryModel, IReadOnlyList<ModelInfoView> Models, IReadOnlyList<AppliedRuleView> Applied, bool ChainLimitReached,
    IReadOnlyList<RuleEvaluationView> Evaluation, IReadOnlyList<IgnoredRuleView> IgnoredRules, string Note);

public sealed record ModelInfoView(string Name, bool Exists, string Type, string? Kind, bool Enabled);

public sealed record AppliedRuleView(Guid RuleId, string Name, string FromModel, string ToModel);

public sealed record RuleEvaluationView(Guid RuleId, string Name, string Scope, int Priority, int ChainStep, string Model, string Outcome, IReadOnlyList<TraceView> Trace)
{
    public static RuleEvaluationView Of(RuleEvaluation e) => new(
        e.RuleId, e.Name, e.Scope.ToString(), e.Priority, e.ChainStep, e.Model, e.Outcome.ToString(),
        [.. e.Trace.Select(t => new TraceView(t.Text, t.LeftValue, t.Result))]);
}

public sealed record TraceView(string Text, string? LeftValue, bool? Result);

public sealed record IgnoredRuleView(Guid RuleId, string RuleName, string Message, int? Position, int? Length)
{
    public static IgnoredRuleView Of(RuleBuildError e) => new(e.RuleId, e.RuleName, e.Message, e.Expression?.Position, e.Expression?.Length);
}

// ---- reports and operations ------------------------------------------------------------------------------

/// <summary>Average and percentiles in milliseconds; null when no request in the bucket had the timing.</summary>
public sealed record LatencyStatsDto(double? Avg, int? P50, int? P90, int? P95, int? P99);

public sealed record FirstTokenStatsDto(double? Avg, int? P50, int? P95);

/// <summary>
/// <c>/api/usage/metrics</c>: the Usage dashboard. Cost is in USD (SEK is only a display currency). <c>RecordsFrom</c> is
/// set when the period reaches back before the oldest kept usage record; the figures start there.
/// </summary>
public sealed record UsageMetricsDto(
    DateTimeOffset From, DateTimeOffset To, string Bucket, string TimeZone, DateTimeOffset? RecordsFrom,
    UsageMetricsTotalsDto Totals, UsageMetricsBucketDto[] Buckets, UsageModelSeriesDto[] Models);

public sealed record UsageMetricsTotalsDto(
    long Requests, long Successes, long ProviderErrors, long Refused, long Cancelled, long Fallbacks, decimal CostUsd,
    long InputTokens, long CachedInputTokens, long OutputTokens, double? CacheHitRate, LatencyStatsDto Latency, LatencyStatsDto Overhead,
    FirstTokenStatsDto FirstToken, double? TokensPerSecond);

public sealed record UsageMetricsBucketDto(
    DateTimeOffset Start, long Requests, long Successes, long ProviderErrors, long Refused, long Cancelled, long Fallbacks, decimal CostUsd,
    long InputTokens, long CachedInputTokens, long OutputTokens, LatencyStatsDto Latency, LatencyStatsDto Overhead,
    double? FirstTokenAvgMs, int? FirstTokenP95Ms, double? TokensPerSecond);

/// <summary>A requested model's requests and cost per bucket (aligned with the buckets); a null model is all the others together.</summary>
public sealed record UsageModelSeriesDto(string? Model, long Requests, decimal CostUsd, long[] RequestsPerBucket, decimal[] CostUsdPerBucket);

public sealed record UsageRequestDto(
    string RequestId, DateTimeOffset Timestamp, string KeyPrefix, string KeyName, string TeamName, string DepartmentName, string Endpoint,
    string RequestedModel, string? ProviderName, string? UpstreamModel, long InputTokens, long CachedInputTokens, long OutputTokens,
    decimal CostSek, int LatencyMs, int StatusCode, RequestOutcome Outcome, int FallbackCount, bool Streamed, PiiPolicy? PiiActionApplied,
    string? PiiCategories, string? ErrorCode, Guid? RoutingRuleId, string? RoutingRuleName, decimal CostUsd, int? UpstreamMs, int? TimeToFirstTokenMs);

/// <summary>
/// <c>MonthlyTotalsBefore</c> is set when the period reaches back before the oldest kept usage record (data retention): usage
/// before that instant comes from monthly totals, whole months that overlap the period, and grouping by day shows those
/// months as <c>yyyy-MM</c>.
/// </summary>
public sealed record UsageSummaryDto(
    DateTimeOffset From, DateTimeOffset To, decimal TotalCostSek, long TotalRequests, long TotalInputTokens, long TotalOutputTokens,
    long TotalErrors, IReadOnlyList<UsageSummaryRow> Rows, DateTimeOffset? MonthlyTotalsBefore = null);

/// <summary>A request in which PII was detected (any policy except <c>Off</c>: <c>Allow</c> counts as "detected and forwarded").</summary>
public sealed record PiiIncidentDto(
    string RequestId, DateTimeOffset Timestamp, Guid KeyId, string KeyPrefix, string KeyName, Guid TeamId, string TeamName, Guid DepartmentId,
    string DepartmentName, string Endpoint, string RequestedModel, string? ProviderName, PiiPolicy Action, IReadOnlyList<PiiFindingDto> Categories,
    RequestOutcome Outcome, int StatusCode, string? RoutingRuleName);

/// <summary>How many values of one category a request contained.</summary>
public sealed record PiiFindingDto(PiiCategory Category, int Count);

/// <summary>Requests (and values found) per category; a request with several categories counts once in each.</summary>
public sealed record PiiCategoryCountDto(PiiCategory Category, long Requests, long Occurrences);

public sealed record PiiActionCountDto(PiiPolicy Action, long Requests);

/// <summary>
/// One page of incidents matching every filter, plus the breakdown per category and action for the period and owner
/// filters only (so the tiles still show every category while one is selected).
/// </summary>
public sealed record PiiIncidentReportDto(
    DateTimeOffset From, DateTimeOffset To, IReadOnlyList<PiiIncidentDto> Items, int Total, long TotalIncidents,
    IReadOnlyList<PiiCategoryCountDto> Categories, IReadOnlyList<PiiActionCountDto> Actions);

public sealed record AuditEntryDto(long Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityType, string? EntityId, string? Details);

public sealed record CatalogDto(string GatewayBaseUrl, IReadOnlyList<CatalogModelDto> Models, IReadOnlyList<CatalogRouteDto> Routes);

public sealed record CatalogModelDto(
    string Name, ModelKind Kind, DataResidency Residency, ProviderCapabilities[] Capabilities, decimal? InputSekPerMillion, decimal? OutputSekPerMillion);

public sealed record CatalogRouteDto(
    string Name, string? Description, ModelKind Kind, DataResidency[] Residencies, ProviderCapabilities[] Capabilities,
    decimal? InputSekPerMillion, decimal? OutputSekPerMillion);

public sealed record OpsHealthDto(
    DateTimeOffset CheckedAt, IReadOnlyList<ComponentHealthDto> Components, VersionsDto Versions, UsageWriterStatus? UsageWriter,
    IReadOnlyList<ProviderHealthDto> Providers);

public sealed record ComponentHealthDto(string Name, string Status, string Description);

public sealed record VersionsDto(string? AdminApi, string? Gateway, string Schema);

/// <summary>
/// A provider's status (from the last hour, see <see cref="ProviderStabilityService.Classify"/>) and its last 24 hours.
/// <c>ErrorRate24h</c> counts every request that did not succeed; <c>ProviderErrorRate24h</c> only the provider's own failures.
/// </summary>
public sealed record ProviderHealthDto(
    Guid Id, string Name, ProviderType Type, DataResidency Residency, bool IsEnabled, bool IsDrained, CircuitState CircuitState,
    long Requests24h, double ErrorRate24h, double FallbackRate24h, int? P50LatencyMs, int? P95LatencyMs,
    double ProviderErrorRate24h, ProviderStatus Status, string StatusReason, long RecentRequests, long RecentProviderErrors);

/// <summary>Provider figures per bucket and a ranking over the whole period (<c>GET /api/ops/providers/stats</c>).</summary>
public sealed record ProviderStabilityDto(
    DateTimeOffset From, DateTimeOffset To, StatsBucket Bucket, string TimeZone, IReadOnlyList<ProviderRankDto> Providers, IReadOnlyList<ProviderBucketDto> Buckets);

/// <summary>A provider over the whole period; <c>Availability</c> is 1 minus its provider error rate.</summary>
public sealed record ProviderRankDto(
    Guid ProviderId, string Name, bool Exists, long Requests, long ProviderErrors, double Availability, double FallbackRate, int? P50LatencyMs, int? P95LatencyMs);

/// <summary>One provider in one bucket that had traffic (buckets without traffic are left out).</summary>
public sealed record ProviderBucketDto(
    Guid ProviderId, DateTimeOffset Start, long Requests, long ProviderErrors, double ErrorRate, long Fallbacks, int? P50LatencyMs, int? P95LatencyMs);

public sealed record ConfigImportResult(int Created, int Updated, int Skipped);
