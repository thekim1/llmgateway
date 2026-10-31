using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.AdminApi.DataAccess;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record DataApiKeyRequest(
    [property: Required, StringLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(10)] List<string> Permissions,
    [property: StringLength(1000)] string? Description = null,
    DateTimeOffset? ExpiresAt = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("Enter a name.", [nameof(Name)]);
        }
        if (Permissions?.Any(p => !DataPermissions.IsKnown(p)) == true)
        {
            yield return new ValidationResult($"Unknown permission. Allowed: {string.Join(", ", DataPermissions.All)}.", [nameof(Permissions)]);
        }
    }
}

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
