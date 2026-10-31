namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>Claims the admin API reads from the signed-in user, after <see cref="AdminAuthentication.ApplyClaimMapping"/>.</summary>
public static class AdminClaims
{
    public const string Subject = "sub";
    public const string Roles = "roles";
    public const string DepartmentCodes = "departmentCodes";
    public const string TeamCodes = "teamCodes";

    /// <summary>A random id given at sign-in and kept while the session lasts (audit entries and security events).</summary>
    public const string Session = "ume_session";
}
