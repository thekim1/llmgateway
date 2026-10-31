namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Metadata about one gateway request. Deliberately contains NO prompt or response content
/// (GDPR data minimisation). PII is recorded as category counts only.
/// </summary>
public sealed class UsageRecord
{
    public long Id { get; set; }
    public required string RequestId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Guid VirtualKeyId { get; set; }
    public Guid TeamId { get; set; }
    public Guid DepartmentId { get; set; }
    public GatewayEndpoint Endpoint { get; set; }
    public required string RequestedModel { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderName { get; set; }
    public Guid? ModelDeploymentId { get; set; }
    public string? UpstreamModel { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Seconds of audio transcribed (audio endpoints only): as reported by the provider, else estimated from the file.</summary>
    public decimal AudioSeconds { get; set; }

    public decimal CostUsd { get; set; }
    /// <summary>End to end: from the gateway receiving the request until its answer was sent (a live session's length).</summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Time spent waiting on providers: every attempt (failed fallbacks included) and reading a streamed answer.
    /// <see cref="LatencyMs"/> minus this is the gateway's own overhead. Null when no provider was called and for live sessions.
    /// </summary>
    public int? UpstreamMs { get; set; }

    /// <summary>Streamed answers: from the gateway receiving the request until the first event reached the client.</summary>
    public int? TimeToFirstTokenMs { get; set; }

    /// <summary>
    /// Streamed answers: from sending the request to the provider that answered until its first event arrived. Failed
    /// fallback attempts, retries and the gateway's own time are left out, and the answer's length does not count, so
    /// this is how quickly the provider responds.
    /// </summary>
    public int? ProviderFirstTokenMs { get; set; }

    public int StatusCode { get; set; }
    public RequestOutcome Outcome { get; set; }
    public int FallbackCount { get; set; }
    public bool Streamed { get; set; }
    public PiiPolicy? PiiActionApplied { get; set; }

    /// <summary>e.g. <c>Personnummer:2,Email:1</c>. Never the matched values.</summary>
    public string? PiiCategories { get; set; }

    /// <summary>
    /// What the content guard flagged, e.g. <c>request:prompt_injection</c> or <c>answer:S1,S10</c> (categories only, never
    /// the text); null when nothing was flagged or the key's guard is off.
    /// </summary>
    public string? GuardFlags { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>The routing rule that decided where the request went (last one when rules were chained). No foreign key: the rule may be deleted later.</summary>
    public Guid? RoutingRuleId { get; set; }

    /// <summary>Name of that rule at the time of the request, so reports stay readable after it is renamed or deleted.</summary>
    public string? RoutingRuleName { get; set; }

    /// <summary>
    /// When the row was written (database transaction start, set by Postgres). Data API feeds only hand out rows older
    /// than their settle window, so a row committed late by a concurrent writer is never skipped by a consumer's cursor.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}
