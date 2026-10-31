using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Reports;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record UsageRequestDto(
    string RequestId, DateTimeOffset Timestamp, string KeyPrefix, string KeyName, string TeamName, string DepartmentName, string Endpoint,
    string RequestedModel, string? ProviderName, string? UpstreamModel, long InputTokens, long CachedInputTokens, long OutputTokens,
    int LatencyMs, int StatusCode, RequestOutcome Outcome, int FallbackCount, bool Streamed, PiiPolicy? PiiActionApplied,
    string? PiiCategories, string? ErrorCode, Guid? RoutingRuleId, string? RoutingRuleName, decimal CostUsd, int? UpstreamMs, int? TimeToFirstTokenMs,
    string? GuardFlags = null, int? ProviderFirstTokenMs = null);

public sealed record UsageSummaryRow(string Key, string Label, long Requests, long InputTokens, long OutputTokens, decimal CostUsd, long Errors, long Fallbacks);

/// <summary>
/// <c>MonthlyTotalsBefore</c> is set when the period reaches back before the oldest kept usage record (data retention): usage
/// before that instant comes from monthly totals, whole months that overlap the period, and grouping by day shows those
/// months as <c>yyyy-MM</c>.
/// </summary>
public sealed record UsageSummaryDto(
    DateTimeOffset From, DateTimeOffset To, decimal TotalCostUsd, long TotalRequests, long TotalInputTokens, long TotalOutputTokens,
    long TotalErrors, IReadOnlyList<UsageSummaryRow> Rows, DateTimeOffset? MonthlyTotalsBefore = null);

public sealed record AuditEntryDto(long Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityType, string? EntityId, string? Details,
    string? ActorName, string? ActorRoles, string? ActorDepartments, string? SourceAddress, string? SessionId, string? RequestId, string? UserAgent);

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

/// <summary>Average and percentiles in milliseconds; null when no request in the bucket had the timing.</summary>
public sealed record LatencyStatsDto(double? Avg, int? P50, int? P90, int? P95, int? P99);

public sealed record FirstTokenStatsDto(double? Avg, int? P50, int? P95);

/// <summary>
/// <c>/api/usage/metrics</c>: the Usage dashboard. Cost is in USD. <c>RecordsFrom</c> is
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
