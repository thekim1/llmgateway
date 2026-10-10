using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Routing;

namespace Ume.LlmGateway.AdminApi;

/// <summary>What a budget or routing rule belongs to: a department, team or key (a global rule has no owner).</summary>
public enum OwnerKind
{
    Department,
    Team,
    VirtualKey,
}

/// <summary>A budget's or routing rule's owner.</summary>
public readonly record struct Owner(OwnerKind Kind, Guid Id)
{
    public static Owner Of(BudgetScope scope, Guid id) => new(scope switch
    {
        BudgetScope.Department => OwnerKind.Department,
        BudgetScope.Team => OwnerKind.Team,
        BudgetScope.VirtualKey => OwnerKind.VirtualKey,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown budget scope."),
    }, id);

    /// <summary><c>null</c> for <see cref="RoutingScope.Global"/> or a missing id.</summary>
    public static Owner? Of(RoutingScope scope, Guid? id) => id is not { } value ? null : scope switch
    {
        RoutingScope.Department => new Owner(OwnerKind.Department, value),
        RoutingScope.Team => new Owner(OwnerKind.Team, value),
        RoutingScope.VirtualKey => new Owner(OwnerKind.VirtualKey, value),
        RoutingScope.Global => null,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown routing scope."),
    };
}

/// <summary>
/// Resolves owners to their names. With a user, only owners in the user's departments are found (budgets and alerts
/// are visible to department admins); without one, every owner is (routing rules are gateway-admin only).
/// One query per owner kind, however many owners are asked for.
/// </summary>
public sealed class OwnerScopeResolver(AdminContext ctx)
{
    public async Task<string?> NameAsync(Owner owner, ClaimsPrincipal? user, CancellationToken ct) =>
        (await NamesAsync([owner], user, ct)).GetValueOrDefault(owner);

    public async Task<bool> ExistsAsync(Owner owner, CancellationToken ct) => await NameAsync(owner, null, ct) is not null;

    public async Task<Dictionary<Owner, string>> NamesAsync(IEnumerable<Owner> owners, ClaimsPrincipal? user, CancellationToken ct)
    {
        var byKind = owners.Distinct().ToLookup(o => o.Kind, o => o.Id);
        var names = new Dictionary<Owner, string>();
        if (byKind[OwnerKind.Department].ToArray() is { Length: > 0 } departments)
        {
            var source = user is null ? ctx.Db.Departments : ctx.Departments(user);
            foreach (var d in await source.AsNoTracking().Where(d => departments.Contains(d.Id)).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            {
                names[new Owner(OwnerKind.Department, d.Id)] = d.Name;
            }
        }

        if (byKind[OwnerKind.Team].ToArray() is { Length: > 0 } teams)
        {
            var source = user is null ? ctx.Db.Teams : ctx.Teams(user);
            foreach (var t in await source.AsNoTracking().Where(t => teams.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToListAsync(ct))
            {
                names[new Owner(OwnerKind.Team, t.Id)] = t.Name;
            }
        }

        if (byKind[OwnerKind.VirtualKey].ToArray() is { Length: > 0 } keys)
        {
            var source = user is null ? ctx.Db.VirtualKeys : ctx.Keys(user);
            foreach (var k in await source.AsNoTracking().Where(k => keys.Contains(k.Id)).Select(k => new { k.Id, k.Name }).ToListAsync(ct))
            {
                names[new Owner(OwnerKind.VirtualKey, k.Id)] = k.Name;
            }
        }

        return names;
    }

    /// <summary>The owner's label in messages ("Teamet finns inte").</summary>
    public static string Label(OwnerKind kind) => kind switch
    {
        OwnerKind.VirtualKey => "Nyckeln",
        OwnerKind.Team => "Teamet",
        OwnerKind.Department => "Förvaltningen",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown owner kind."),
    };
}
