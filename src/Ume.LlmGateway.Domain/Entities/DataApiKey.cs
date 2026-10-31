namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// A key for the read-only Data API (docs/data-access.md), for integrations that cannot use OAuth client credentials.
/// Managed by gateway admins; holds only the hash of the key (<see cref="Keys.DataApiKeyHasher"/>), never the key.
/// </summary>
public sealed class DataApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Non-secret identifying prefix, e.g. <c>ume-dk-AbC1</c>, shown in the UI.</summary>
    public required string Prefix { get; set; }

    /// <summary>Hex SHA-256 of the full key.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Granted Data API permissions (<see cref="DataPermissions"/>).</summary>
    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Written by the Data API at most every few minutes per key, so it is approximate.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Active, Expired or Revoked.</summary>
    public KeyStatus GetStatus(DateTimeOffset now) =>
        RevokedAt is { } revoked && revoked <= now ? KeyStatus.Revoked
        : ExpiresAt is { } expires && expires <= now ? KeyStatus.Expired
        : KeyStatus.Active;
}
