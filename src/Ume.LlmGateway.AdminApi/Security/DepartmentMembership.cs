using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>An active department's cost-centre code and the group claim values whose members belong to it.</summary>
public sealed record DepartmentGroups(string Code, IReadOnlyList<string> MemberGroups);

/// <summary>
/// Department scope from group membership: a user whose group claim holds one of a department's member groups gets that
/// department's code, as if the department claim had listed it. Only the token is read, so a group the token does not
/// show (or a token without the group claim, as Entra sends above its group limit) adds nothing: the scope can be
/// narrower than the configuration says, never wider. Membership never gives a role.
/// </summary>
public static class DepartmentMembership
{
    /// <summary>The session's property holding the codes that came from groups (a JSON array), for <c>/bff/user</c>.</summary>
    public const string GroupCodesItem = ".ume.departmentGroupCodes";

    /// <summary>
    /// The codes of the departments the groups give, without those the department claim already holds. Groups are matched
    /// like <see cref="OidcOptions.RoleGroups"/> (ignoring case), codes as the scoped queries compare them (exactly).
    /// </summary>
    public static IReadOnlyList<string> CodesFromGroups(IReadOnlySet<string> groups, IReadOnlyCollection<string> claimCodes, IEnumerable<DepartmentGroups> departments)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(claimCodes);
        ArgumentNullException.ThrowIfNull(departments);
        if (groups.Count == 0)
        {
            return [];
        }

        var known = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        return [.. departments
            .Where(d => !claimCodes.Contains(d.Code) && d.MemberGroups.Any(known.Contains))
            .Select(d => d.Code)
            .Distinct(StringComparer.Ordinal)];
    }

    public static void Store(AuthenticationProperties properties, IReadOnlyList<string> groupCodes)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(groupCodes);
        if (groupCodes.Count > 0)
        {
            properties.Items[GroupCodesItem] = JsonSerializer.Serialize(groupCodes);
        }
    }

    /// <summary>Each of the session's department codes with where it came from: <c>claim</c> or <c>group</c>.</summary>
    public static IReadOnlyList<DepartmentSource> Sources(IEnumerable<string> departmentCodes, AuthenticationProperties? properties)
    {
        ArgumentNullException.ThrowIfNull(departmentCodes);
        var fromGroups = properties?.Items.TryGetValue(GroupCodesItem, out var json) == true && json is not null
            ? JsonSerializer.Deserialize<HashSet<string>>(json) ?? []
            : [];
        return [.. departmentCodes.Select(code => new DepartmentSource(code, fromGroups.Contains(code) ? "group" : "claim"))];
    }
}

public sealed record DepartmentSource(string Code, string Source);
