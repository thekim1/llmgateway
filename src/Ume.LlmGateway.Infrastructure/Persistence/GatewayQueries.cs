using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Persistence;

/// <summary>
/// Queries shared by the gateway and the admin API, so both read budgets and key lineage the same way.
/// </summary>
public static class GatewayQueries
{
    /// <summary>The currency provider prices are entered in.</summary>
    public const string PriceCurrency = "USD";

    /// <summary>Rotated key id → its replacement, for <see cref="KeyRotation"/>.</summary>
    public static Task<Dictionary<Guid, Guid>> KeyRotationMapAsync(this GatewayDbContext db, CancellationToken cancellationToken) =>
        db.VirtualKeys.AsNoTracking()
            .Where(k => k.RotatedToKeyId != null)
            .ToDictionaryAsync(k => k.Id, k => k.RotatedToKeyId!.Value, cancellationToken);

    /// <summary>The oldest instant usage records cover after the retention job (<see cref="GatewaySettings.UsageRetainedFrom"/>); null when nothing was removed.</summary>
    public static Task<DateTimeOffset?> UsageRetainedFromAsync(this GatewayDbContext db, CancellationToken cancellationToken) =>
        db.Settings.AsNoTracking().Select(s => s.UsageRetainedFrom).SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Cost in <paramref name="window"/> per key, team and department: usage records from <paramref name="retainedFrom"/> on, and
    /// before it the monthly totals of months that start in the window. Budget windows longer than the minimum retention
    /// start on a month, so the sum is exact.
    /// </summary>
    public static IQueryable<UsageCost> CostsIn(this GatewayDbContext db, PeriodWindow window, DateTimeOffset? retainedFrom)
    {
        var recordsFrom = retainedFrom is { } r && r > window.Start ? r : window.Start;
        var records = db.UsageRecords.AsNoTracking().Where(u => u.Timestamp >= recordsFrom && u.Timestamp < window.End)
            .Select(u => new UsageCost
            {
                VirtualKeyId = u.VirtualKeyId, TeamId = u.TeamId, DepartmentId = u.DepartmentId, CostUsd = u.CostUsd,
                Tokens = u.InputTokens + u.OutputTokens, ProviderAccountId = u.ProviderAccountId,
            });
        if (retainedFrom is not { } kept || kept <= window.Start)
        {
            return records;
        }

        var totalsUntil = kept < window.End ? kept : window.End;
        return records.Concat(db.UsageMonthlyTotals.AsNoTracking().Where(t => t.MonthStart >= window.Start && t.MonthStart < totalsUntil)
            .Select(t => new UsageCost
            {
                VirtualKeyId = t.VirtualKeyId, TeamId = t.TeamId, DepartmentId = t.DepartmentId, CostUsd = t.CostUsd,
                Tokens = t.InputTokens + t.OutputTokens, ProviderAccountId = t.ProviderAccountId,
            }));
    }

    /// <summary>
    /// What was used against <paramref name="budget"/> in <paramref name="window"/>, in its unit: USD, or input plus output
    /// tokens (of providers of the budget's residency, when it has one). A key budget covers the key and every key it was
    /// rotated into. Pass <paramref name="rotations"/> when it is already loaded (e.g. when summing many budgets).
    /// </summary>
    public static async Task<decimal> SpentAsync(this GatewayDbContext db, Budget budget, PeriodWindow window, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, Guid>? rotations = null)
    {
        var query = db.CostsIn(window, await db.UsageRetainedFromAsync(cancellationToken));
        switch (budget.Scope)
        {
            case BudgetScope.Department:
                query = query.Where(u => u.DepartmentId == budget.ScopeId);
                break;
            case BudgetScope.Team:
                query = query.Where(u => u.TeamId == budget.ScopeId);
                break;
            case BudgetScope.VirtualKey:
                var keyIds = KeyRotation.Descendants(budget.ScopeId, rotations ?? await db.KeyRotationMapAsync(cancellationToken)).ToArray();
                query = query.Where(u => keyIds.Contains(u.VirtualKeyId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(budget), budget.Scope, "Unknown budget scope.");
        }

        if (budget.Unit != BudgetUnit.Tokens)
        {
            return await query.SumAsync(u => u.CostUsd, cancellationToken);
        }

        if (budget.Residency is { } residency)
        {
            var providers = await db.ProviderAccounts.AsNoTracking().Where(p => p.Residency == residency).Select(p => p.Id).ToArrayAsync(cancellationToken);
            query = query.Where(u => u.ProviderAccountId != null && providers.Contains(u.ProviderAccountId.Value));
        }

        return await query.SumAsync(u => (decimal)u.Tokens, cancellationToken);
    }
}

/// <summary>One usage record's or monthly total's cost and owners (<see cref="GatewayQueries.CostsIn"/>).</summary>
public sealed class UsageCost
{
    public Guid VirtualKeyId { get; init; }
    public Guid TeamId { get; init; }
    public Guid DepartmentId { get; init; }
    public decimal CostUsd { get; init; }

    /// <summary>Input plus output tokens, for token budgets.</summary>
    public long Tokens { get; init; }

    /// <summary>The provider that served it, for token budgets limited to one residency.</summary>
    public Guid? ProviderAccountId { get; init; }
}
