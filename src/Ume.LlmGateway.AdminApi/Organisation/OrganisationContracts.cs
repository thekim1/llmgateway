using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.AdminApi.Organisation;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record DepartmentRequest(
    [property: Required, StringLength(200)] string Name,
    [property: Required, StringLength(50)] string CostCenterCode,
    bool IsActive = true) : AdminRequest;

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

public sealed record DepartmentDto(Guid Id, string Name, string CostCenterCode, bool IsActive, int TeamCount, DateTimeOffset CreatedAt)
{
    public static DepartmentDto Of(Department d) => new(d.Id, d.Name, d.CostCenterCode, d.IsActive, d.Teams.Count, d.CreatedAt);
}

public sealed record TeamDto(Guid Id, Guid DepartmentId, string? DepartmentName, string Name, string? Description, bool IsActive, int KeyCount, DateTimeOffset CreatedAt,
    RequestPolicy? RequestPolicy = null, string? Code = null)
{
    public static TeamDto Of(Team t) => new(t.Id, t.DepartmentId, t.Department?.Name, t.Name, t.Description, t.IsActive, t.VirtualKeys.Count, t.CreatedAt, t.RequestPolicy, t.Code);
}
