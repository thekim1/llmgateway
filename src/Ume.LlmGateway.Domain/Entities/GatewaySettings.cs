using Ume.LlmGateway.Domain.Usage;

namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Gateway-wide settings edited on the admin UI's Settings page. One row (<see cref="SingletonId"/>); no row means the
/// defaults. The gateway reads it with the catalogue, so a change takes effect when the admin API publishes it.
/// </summary>
public sealed class GatewaySettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Reverse proxies whose <c>X-Forwarded-For</c> the gateway believes: single addresses or CIDR networks in canonical form.
    /// Empty = the connection address is the client address and the header is ignored.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>
    /// Log every request at Information, successful ones too (normally only refused, failed and cancelled requests are).
    /// Costs throughput while on; every request is in the usage records either way.
    /// </summary>
    public bool LogAllRequests { get; set; }

    /// <summary>When <see cref="LogAllRequests"/> switches itself off; null = it stays on until it is turned off.</summary>
    public DateTimeOffset? LogAllRequestsUntil { get; set; }

    /// <summary>Whether every request is logged at <paramref name="now"/>.</summary>
    public bool LogsAllRequestsAt(DateTimeOffset now) => LogAllRequests && (LogAllRequestsUntil is not { } until || now < until);

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Days usage records, refused-credential records and budget alerts are kept (<see cref="RetentionPolicy"/>). Whole
    /// months are removed, after their monthly totals are saved in <see cref="UsageMonthlyTotal"/>.
    /// </summary>
    public int UsageRetentionDays { get; set; } = RetentionPolicy.DefaultDays;

    /// <summary>Days admin audit entries are kept; null keeps them for ever.</summary>
    public int? AuditRetentionDays { get; set; } = RetentionPolicy.DefaultDays;

    /// <summary>
    /// The oldest instant usage records cover (a Europe/Stockholm month start), set by the retention job; null until it
    /// first removes anything. Usage before it is only in <see cref="UsageMonthlyTotal"/>, so reports and budget spend read
    /// totals before it and records from it on. Written only by the database function <c>ume_retention_step</c>.
    /// </summary>
    public DateTimeOffset? UsageRetainedFrom { get; set; }

    public DateTimeOffset? RetentionLastRunAt { get; set; }

    /// <summary>Rows removed by the last run, all tables together.</summary>
    public long? RetentionLastRunDeleted { get; set; }

    /// <summary>Why the last run failed (no exception text, which could hold connection details); null when it succeeded.</summary>
    public string? RetentionLastRunError { get; set; }
}
