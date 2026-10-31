namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Refused credentials, counted per (reason, endpoint, key, source address) over a short interval instead of one row
/// per request: unauthenticated callers must not be able to grow the table at will. Never holds the presented key.
/// </summary>
public sealed class AuthFailure
{
    public long Id { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public AuthFailureReason Reason { get; set; }
    public GatewayEndpoint Endpoint { get; set; }

    /// <summary>Set only when the presented key was recognised (revoked, expired, disabled, owner inactive).</summary>
    public Guid? VirtualKeyId { get; set; }

    public string? KeyPrefix { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Client address, truncated or omitted according to <c>Gateway:Security:SourceAddress</c>.</summary>
    public string? SourceAddress { get; set; }

    public int Count { get; set; }

    /// <summary>
    /// When the row was written (database transaction start, set by Postgres). Data API feeds only hand out rows older
    /// than their settle window, so a row committed late by a concurrent writer is never skipped by a consumer's cursor.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}
