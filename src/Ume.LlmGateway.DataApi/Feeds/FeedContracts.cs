using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.DataApi.Feeds;

public sealed record UsageRecordItem(
    long Id, string RequestId, DateTimeOffset Timestamp, Guid KeyId, Guid TeamId, Guid DepartmentId, GatewayEndpoint Endpoint,
    string RequestedModel, Guid? ProviderId, string? ProviderName, Guid? ModelId, string? UpstreamModel,
    long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, int LatencyMs, int StatusCode,
    RequestOutcome Outcome, int FallbackCount, bool Streamed, PiiPolicy? PiiAction, string? PiiCategories, string? ErrorCode,
    Guid? RoutingRuleId, string? RoutingRuleName, DateTimeOffset RecordedAt, decimal AudioSeconds, int? UpstreamMs, int? TimeToFirstTokenMs,
    int? ProviderFirstTokenMs) : IFeedItem;

public sealed record SecurityRequestItem(
    long Id, string RequestId, DateTimeOffset Timestamp, Guid KeyId, Guid TeamId, Guid DepartmentId, GatewayEndpoint Endpoint,
    RequestOutcome Outcome, int StatusCode, string? ErrorCode, PiiPolicy? PiiAction, string? PiiCategories, DateTimeOffset RecordedAt) : IFeedItem;

public sealed record AuthFailureItem(
    long Id, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, AuthFailureReason Reason, GatewayEndpoint Endpoint, Guid? KeyId,
    string? KeyPrefix, Guid? TeamId, Guid? DepartmentId, string? SourceAddress, int Count, DateTimeOffset RecordedAt) : IFeedItem;

public sealed record AuditItem(
    long Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityType, string? EntityId, string? Details, DateTimeOffset RecordedAt,
    string? ActorName, string? ActorRoles, string? ActorDepartments, string? SourceAddress, string? SessionId, string? RequestId, string? UserAgent) : IFeedItem;

public sealed record KeyInventoryItem(
    Guid Id, string Prefix, string Name, Guid TeamId, Guid? DepartmentId, KeyStatus Status, DateTimeOffset CreatedAt, string? CreatedBy,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? GraceUntil, Guid? RotatedToKeyId, DateTimeOffset? LastUsedAt,
    PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, IReadOnlyList<string> AllowedModels, IReadOnlyList<string> AllowedProviders,
    IReadOnlyList<DataResidency> AllowedResidencies, int? RequestsPerMinute, int? TokensPerMinute, IReadOnlyList<string> AllowedIpRanges,
    bool AllowProviderTools, bool AllowProviderStorage, int? MaxConcurrentRequests);

public sealed record AggregateItem(
    DateOnly Period, Guid? DepartmentId, string? CostCenterCode, string? DepartmentName, Guid? ModelId, string? ModelName, Guid? ProviderId,
    string? ProviderName, DataResidency? Residency, string Endpoint, long Requests, long SuccessfulRequests, long InputTokens,
    long CachedInputTokens, long OutputTokens, decimal CostUsd, decimal AudioSeconds);

/// <summary>Raw result of the aggregate query; names are joined in afterwards.</summary>
public sealed class AggregateRow
{
    public DateOnly Period { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ModelId { get; set; }
    public Guid? ProviderId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public long Requests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public decimal AudioSeconds { get; set; }
}
