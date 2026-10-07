using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

public sealed record BudgetCheck(Budget Budget, PeriodWindow Window, SpendCounter Counter);

public sealed class BudgetReservation
{
    public static BudgetReservation None { get; } = new([], 0, null, null);

    public BudgetReservation(IReadOnlyList<BudgetCheck> checks, long reservedMicroSek, BudgetCheck? exceeded, decimal? remainingSek)
    {
        Checks = checks;
        ReservedMicroSek = reservedMicroSek;
        Exceeded = exceeded;
        RemainingSek = remainingSek;
    }

    public IReadOnlyList<BudgetCheck> Checks { get; }
    public long ReservedMicroSek { get; }
    public BudgetCheck? Exceeded { get; }

    /// <summary>Smallest remaining amount over all applicable budgets (before this request), or null if no budgets.</summary>
    public decimal? RemainingSek { get; }

    public bool Allowed => Exceeded is null;
}

public sealed record AlertCandidate(Budget Budget, PeriodWindow Window, int ThresholdPercent, decimal SpentSek);

/// <summary>
/// Hierarchical budget enforcement (key → team → förvaltning). Uses shared counters with
/// reserve-then-reconcile so concurrent requests cannot all slip past a nearly exhausted budget.
/// </summary>
public sealed class BudgetService(ISpendLedger ledger, IServiceScopeFactory scopes, TimeProvider time)
{
    public const long MicroPerSek = 1_000_000;

    public static IReadOnlyList<Budget> ApplicableBudgets(CatalogSnapshot catalog, VirtualKey key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(key);
        var departmentId = key.Team?.DepartmentId ?? Guid.Empty;
        return
        [
            .. KeyRotation.Ancestors(key.Id, catalog.KeyReplacements).SelectMany(id => catalog.Budgets[(BudgetScope.VirtualKey, id)]),
            .. catalog.Budgets[(BudgetScope.Team, key.TeamId)],
            .. catalog.Budgets[(BudgetScope.Department, departmentId)],
        ];
    }

    public async Task<BudgetReservation> ReserveAsync(IReadOnlyList<Budget> budgets, decimal estimatedSek, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.Count == 0)
        {
            return BudgetReservation.None;
        }

        var now = time.GetUtcNow();
        var checks = budgets.Select(b =>
        {
            var window = BudgetPeriods.GetWindow(b.Period, now);
            var key = SpendCounter.KeyFor(b.Scope, b.ScopeId, b.Period, window.Start);
            return new BudgetCheck(b, window, new SpendCounter(key, ToMicro(b.LimitSek), window.End - now + TimeSpan.FromDays(1)));
        }).ToList();

        // Seed counters missing from the shared store (cold start / new period) from the usage table.
        var values = await ledger.GetAsync([.. checks.Select(c => c.Counter.Key)], cancellationToken);
        for (var i = 0; i < checks.Count; i++)
        {
            if (values[i] is null)
            {
                var spent = await SpentFromDatabaseAsync(checks[i].Budget, checks[i].Window, cancellationToken);
                await ledger.InitializeAsync(checks[i].Counter.Key, ToMicro(spent), checks[i].Counter.TimeToLive, cancellationToken);
            }
        }

        var amount = Math.Max(0, ToMicro(estimatedSek));
        var index = await ledger.TryReserveAsync([.. checks.Select(c => c.Counter)], amount, cancellationToken);
        var current = await ledger.GetAsync([.. checks.Select(c => c.Counter.Key)], cancellationToken);
        var remaining = checks.Select((c, i) => (c.Counter.LimitMicroSek - (current[i] ?? 0) + (index < 0 ? amount : 0)) / (decimal)MicroPerSek).Min();
        return index >= 0
            ? new BudgetReservation(checks, 0, checks[index], Math.Max(0, remaining))
            : new BudgetReservation(checks, amount, null, Math.Max(0, remaining));
    }

    /// <summary>Replaces the reservation with the actual cost. Returns alert thresholds crossed by this request.</summary>
    public async Task<IReadOnlyList<AlertCandidate>> CommitAsync(BudgetReservation reservation, decimal actualSek, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.Checks.Count == 0 || !reservation.Allowed)
        {
            return [];
        }

        var actual = ToMicro(actualSek);
        var after = await ledger.AddAsync([.. reservation.Checks.Select(c => c.Counter)], actual - reservation.ReservedMicroSek, cancellationToken);
        var alerts = new List<AlertCandidate>();
        for (var i = 0; i < reservation.Checks.Count; i++)
        {
            var check = reservation.Checks[i];
            var afterSek = after[i] / (decimal)MicroPerSek;
            var beforeSek = afterSek - actualSek;
            foreach (var threshold in BudgetEvaluator.CrossedThresholds(check.Budget, beforeSek, afterSek))
            {
                alerts.Add(new AlertCandidate(check.Budget, check.Window, threshold, afterSek));
            }
        }

        return alerts;
    }

    private async Task<decimal> SpentFromDatabaseAsync(Budget budget, PeriodWindow window, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var query = db.UsageRecords.AsNoTracking().Where(u => u.Timestamp >= window.Start && u.Timestamp < window.End);
        var keyIds = budget.Scope == BudgetScope.VirtualKey
            ? KeyRotation.Descendants(budget.ScopeId, await db.VirtualKeys.Where(k => k.RotatedToKeyId != null)
                .ToDictionaryAsync(k => k.Id, k => k.RotatedToKeyId!.Value, cancellationToken)).ToArray()
            : [];
        query = budget.Scope switch
        {
            BudgetScope.Department => query.Where(u => u.DepartmentId == budget.ScopeId),
            BudgetScope.Team => query.Where(u => u.TeamId == budget.ScopeId),
            _ => query.Where(u => keyIds.Contains(u.VirtualKeyId)),
        };
        return await query.SumAsync(u => u.CostSek, cancellationToken);
    }

    public static long ToMicro(decimal sek) => (long)decimal.Round(sek * MicroPerSek, MidpointRounding.AwayFromZero);
}
