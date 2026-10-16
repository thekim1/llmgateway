using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Persistence;

/// <summary>
/// Queries shared by the gateway and the admin API, so both read budgets, key lineage and exchange rates the same way.
/// </summary>
public static class GatewayQueries
{
    /// <summary>The currency provider prices are entered in.</summary>
    public const string PriceCurrency = "USD";

    /// <summary>The exchange rate in effect at <paramref name="now"/>, or <c>null</c> when none has been entered.</summary>
    public static Task<ExchangeRate?> CurrentRateAsync(this GatewayDbContext db, DateTimeOffset now, CancellationToken cancellationToken, string currency = PriceCurrency) =>
        db.ExchangeRates.AsNoTracking()
            .Where(r => r.Currency == currency && r.EffectiveFrom <= now)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

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
            .Select(u => new UsageCost { VirtualKeyId = u.VirtualKeyId, TeamId = u.TeamId, DepartmentId = u.DepartmentId, CostUsd = u.CostUsd });
        if (retainedFrom is not { } kept || kept <= window.Start)
        {
            return records;
        }

        var totalsUntil = kept < window.End ? kept : window.End;
        return records.Concat(db.UsageMonthlyTotals.AsNoTracking().Where(t => t.MonthStart >= window.Start && t.MonthStart < totalsUntil)
            .Select(t => new UsageCost { VirtualKeyId = t.VirtualKeyId, TeamId = t.TeamId, DepartmentId = t.DepartmentId, CostUsd = t.CostUsd }));
    }

    /// <summary>
    /// USD spent against <paramref name="budget"/> in <paramref name="window"/>. A key budget covers the key and every key it was rotated into.
    /// Pass <paramref name="rotations"/> when it is already loaded (e.g. when summing many budgets).
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

        return await query.SumAsync(u => u.CostUsd, cancellationToken);
    }
}

/// <summary>One usage record's or monthly total's cost and owners (<see cref="GatewayQueries.CostsIn"/>).</summary>
public sealed class UsageCost
{
    public Guid VirtualKeyId { get; init; }
    public Guid TeamId { get; init; }
    public Guid DepartmentId { get; init; }
    public decimal CostUsd { get; init; }
}
