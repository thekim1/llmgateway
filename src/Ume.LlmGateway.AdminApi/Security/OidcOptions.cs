using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>
/// Bound from the <c>Oidc</c> configuration section. Defaults match the bundled Keycloak realm; other
/// identity providers (Keycloak with different mappers, AD FS, Entra ID) can remap claims or map groups to roles.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The identity provider's issuer URL.</summary>
    [Required]
    public string? Authority { get; set; }

    [Required]
    public string ClientId { get; set; } = "ume-admin";

    public string? ClientSecret { get; set; }

    public string RoleClaim { get; set; } = "roles";
    public string DepartmentClaim { get; set; } = "departmentCodes";

    /// <summary>Claim holding the codes of the teams a team admin manages. Set it to the group claim to use group names as team codes.</summary>
    public string TeamClaim { get; set; } = "teamCodes";

    public string GroupClaim { get; set; } = "groups";
    /// <summary>Role name (gateway-admin, department-admin, viewer, team-admin) to the group names that grant it, separated by ';'.</summary>
    public Dictionary<string, string> RoleGroups { get; set; } = [];
    /// <summary>Extra scopes to request, for example "groups".</summary>
    public string[] ExtraScopes { get; set; } = [];
}
