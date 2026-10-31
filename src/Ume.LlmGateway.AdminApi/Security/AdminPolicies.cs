using Ume.LlmGateway.AdminApi.Common;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>Authorization and rate-limiting policy names.</summary>
public static class AdminPolicies
{
    /// <summary>Gateway admins only.</summary>
    public const string Admin = "admin";

    /// <summary>Gateway and department admins (department admins within their departments).</summary>
    public const string Manage = "manage";

    /// <summary>Every admin console role except team admin, viewers included.</summary>
    public const string Read = "read";

    /// <summary>Key management: gateway admins, department admins in their departments, team admins in their teams.</summary>
    public const string ManageKeys = "manage-keys";

    /// <summary>Listing teams: every role that reads or manages keys.</summary>
    public const string ListTeams = "list-teams";

    /// <summary>The per-user fixed-window rate limit on <c>/api</c> (<see cref="AdminOptions.RequestsPerMinute"/>).</summary>
    public const string PerUserRateLimit = "per-user";
}
