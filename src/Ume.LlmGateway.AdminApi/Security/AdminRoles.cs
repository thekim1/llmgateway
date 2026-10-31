namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>Admin console roles, from the identity provider's role claim (see docs/identity-and-roles.md).</summary>
public static class AdminRoles
{
    public const string GatewayAdmin = "gateway-admin";
    public const string DepartmentAdmin = "department-admin";
    public const string Viewer = "viewer";

    /// <summary>Manages the keys of the teams in the user's <see cref="AdminClaims.TeamCodes"/>, and nothing else.</summary>
    public const string TeamAdmin = "team-admin";
}
