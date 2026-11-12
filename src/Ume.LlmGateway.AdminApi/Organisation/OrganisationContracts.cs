using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Requests;

namespace Ume.LlmGateway.AdminApi.Organisation;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record DepartmentRequest(
    [property: Required, StringLength(200)] string Name,
    [property: Required, StringLength(50)] string CostCenterCode,
    bool IsActive = true,
    // Values of the identity provider's group claim whose members get this department's scope. Omitted = unchanged; [] clears it.
    List<string>? MemberGroups = null) : AdminRequest, IValidatableObject
{
    public const int MaxMemberGroups = 50;
    public const int MaxMemberGroupLength = 256;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MemberGroups is not { } groups)
        {
            yield break;
        }
        if (groups.Count > MaxMemberGroups)
        {
            yield return new ValidationResult($"A department can have at most {MaxMemberGroups} member groups.", [nameof(MemberGroups)]);
        }
        if (groups.Any(g => g is null || g.Trim() is not { Length: > 0 and <= MaxMemberGroupLength } trimmed || trimmed.Any(char.IsControl)))
        {
            yield return new ValidationResult($"A member group is 1 to {MaxMemberGroupLength} characters, without control characters.", [nameof(MemberGroups)]);
        }
    }

    /// <summary>Trimmed, without duplicates (compared as the group claim is matched: ignoring case).</summary>
    public List<string>? NormalisedMemberGroups() =>
        MemberGroups is null ? null : [.. MemberGroups.Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
}

public sealed record TeamRequest(
    Guid DepartmentId,
    [property: Required, StringLength(200)] string Name,
    [property: StringLength(1000)] string? Description = null,
    bool IsActive = true,
    // Applies to the team's keys. Omitted = unchanged; {} clears it.
    RequestPolicy? RequestPolicy = null,
    // Matched against team admins' teamCodes claim. Omitted = unchanged; "" clears it.
    [property: StringLength(100)] string? Code = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var message in RequestPolicy?.Validate() ?? [])
        {
            yield return new ValidationResult(message, [nameof(RequestPolicy)]);
        }
        if (Code is not null && Code.Trim().Any(char.IsControl))
        {
            yield return new ValidationResult("The team code cannot contain control characters.", [nameof(Code)]);
        }
    }
}

public sealed record DepartmentDto(Guid Id, string Name, string CostCenterCode, bool IsActive, int TeamCount, DateTimeOffset CreatedAt,
    IReadOnlyList<string> MemberGroups)
{
    public static DepartmentDto Of(Department d) => new(d.Id, d.Name, d.CostCenterCode, d.IsActive, d.Teams.Count, d.CreatedAt, d.MemberGroups);
}

public sealed record TeamDto(Guid Id, Guid DepartmentId, string? DepartmentName, string Name, string? Description, bool IsActive, int KeyCount, DateTimeOffset CreatedAt,
    RequestPolicy? RequestPolicy = null, string? Code = null)
{
    public static TeamDto Of(Team t) => new(t.Id, t.DepartmentId, t.Department?.Name, t.Name, t.Description, t.IsActive, t.VirtualKeys.Count, t.CreatedAt, t.RequestPolicy, t.Code);
}
