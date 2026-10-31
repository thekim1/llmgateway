using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.AdminApi.Keys;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record KeyRequest(
    Guid TeamId,
    [property: Required, StringLength(200)] string Name,
    [property: Required, MaxLength(1000)] List<string> AllowedModels,
    [property: Required, MaxLength(3)] List<DataResidency> AllowedResidencies,
    [property: EnumDataType(typeof(PiiPolicy))] PiiPolicy PiiPolicy,
    [property: StringLength(1000)] string? Description = null,
    DateTimeOffset? ExpiresAt = null,
    [property: Range(1, int.MaxValue)] int? RequestsPerMinute = null,
    [property: Range(1, int.MaxValue)] int? TokensPerMinute = null,
    bool IsEnabled = true,
    [property: MaxLength(100)] List<string>? AllowedProviders = null,
    [property: EnumDataType(typeof(AttachmentPolicy))] AttachmentPolicy? AttachmentPolicy = null,
    [property: MaxLength(NetworkRanges.MaxEntries)] List<string>? AllowedIpRanges = null,
    bool? AllowProviderTools = null,
    bool? AllowProviderStorage = null,
    [property: Range(1, 100_000)] int? MaxConcurrentRequests = null,
    // On top of the team's policy. Omitted = unchanged on update; {} clears it.
    RequestPolicy? RequestPolicy = null,
    // The on-prem content guard (docs/content-guard.md). Omitted = Off on create, unchanged on update.
    [property: EnumDataType(typeof(ContentGuardPolicy))] ContentGuardPolicy? ContentGuardPolicy = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedModels?.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 200) == true)
        {
            yield return new ValidationResult("Model names must be 1–200 characters.", [nameof(AllowedModels)]);
        }
        if (AllowedProviders?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 100) == true)
        {
            yield return new ValidationResult("Provider names must be 1–100 characters.", [nameof(AllowedProviders)]);
        }
        if (AllowedIpRanges is not null && NetworkRanges.Normalize(AllowedIpRanges, out _) is { } rangeError)
        {
            yield return new ValidationResult(rangeError, [nameof(AllowedIpRanges)]);
        }
        if (AllowedResidencies?.Any(r => !Enum.IsDefined(r)) == true)
        {
            yield return new ValidationResult("The data residency is invalid.", [nameof(AllowedResidencies)]);
        }
        foreach (var message in RequestPolicy?.Validate() ?? [])
        {
            yield return new ValidationResult(message, [nameof(RequestPolicy)]);
        }
    }
}

public sealed record RotateRequest([property: EnumDataType(typeof(KeyRotationMode))] KeyRotationMode Mode = KeyRotationMode.RevokeImmediately) : AdminRequest;

public enum RevealPurpose { Reveal, Copy }

public sealed record RevealRequest([property: EnumDataType(typeof(RevealPurpose))] RevealPurpose Purpose = RevealPurpose.Reveal) : AdminRequest;

public sealed record KeyDto(
    Guid Id, Guid TeamId, string? TeamName, Guid? DepartmentId, string? DepartmentName, string Name, string? Description, string Prefix,
    KeyStatus Status, bool IsEnabled, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? GraceUntil, DateTimeOffset? LastUsedAt, IReadOnlyList<string> AllowedModels, IReadOnlyList<DataResidency> AllowedResidencies,
    IReadOnlyList<string> AllowedProviders, PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, int? RequestsPerMinute, int? TokensPerMinute,
    Guid? RotatedToKeyId, bool CanReveal, IReadOnlyList<string> AllowedIpRanges, bool AllowProviderTools, bool AllowProviderStorage,
    int? MaxConcurrentRequests = null, RequestPolicy? RequestPolicy = null, ContentGuardPolicy ContentGuardPolicy = ContentGuardPolicy.Off)
{
    public static KeyDto Of(VirtualKey k, DateTimeOffset now) => new(
        k.Id, k.TeamId, k.Team?.Name, k.Team?.DepartmentId, k.Team?.Department?.Name, k.Name, k.Description, k.Prefix,
        k.GetStatus(now), k.IsEnabled, k.CreatedAt, k.CreatedBy, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.LastUsedAt,
        k.AllowedModels, k.AllowedResidencies, k.AllowedProviders, k.PiiPolicy, k.AttachmentPolicy, k.RequestsPerMinute, k.TokensPerMinute,
        k.RotatedToKeyId, k.EncryptedSecret is not null, k.AllowedIpRanges, k.AllowProviderTools, k.AllowProviderStorage, k.MaxConcurrentRequests,
        k.RequestPolicy, k.ContentGuardPolicy);
}

/// <summary>A new key with its secret, which is shown this once.</summary>
public sealed record KeyCreatedDto(KeyDto Key, string Secret);

public sealed record KeyRotatedDto(KeyDto Key, string Secret, KeyDto PreviousKey);

public sealed record KeySecretDto(string Secret);
