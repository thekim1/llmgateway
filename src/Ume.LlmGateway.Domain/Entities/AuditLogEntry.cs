namespace Ume.LlmGateway.Domain.Entities;

public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }

    /// <summary>JSON details; secrets are always masked before being written.</summary>
    public string? Details { get; set; }

    /// <summary>The actor's display name or e-mail when the entry was written; the subject alone may be gone from the IdP later.</summary>
    public string? ActorName { get; set; }

    /// <summary>The actor's admin roles at the time, comma-separated.</summary>
    public string? ActorRoles { get; set; }

    /// <summary>The actor's department codes at the time, comma-separated.</summary>
    public string? ActorDepartments { get; set; }

    /// <summary>The actor's client address behind trusted proxies, as <c>Admin:AuditSourceAddress</c> keeps it.</summary>
    public string? SourceAddress { get; set; }

    /// <summary>The sign-in session (a random id from sign-in, never the cookie): ties entries of one session together.</summary>
    public string? SessionId { get; set; }

    /// <summary>The request's trace id, to find it in logs and traces.</summary>
    public string? RequestId { get; set; }

    /// <summary>The browser or client, at most 200 characters.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// When the row was written (database transaction start, set by Postgres). Data API feeds only hand out rows older
    /// than their settle window, so a row committed late by a concurrent writer is never skipped by a consumer's cursor.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}
