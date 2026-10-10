using System.Collections.Concurrent;
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

    /// <summary>Seeding in progress per counter key, so a cold counter is summed from Postgres once, not once per concurrent request.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task>> _seeding = new(StringComparer.Ordinal);

    public static IReadOnlyList<Budget> ApplicableBudgets(CatalogSnapshot catalog, VirtualKey key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(key);
        var departmentId = key.Team?.DepartmentId ?? Guid.Empty;
        return
        [
            .. catalog.KeyLineage(key.Id).SelectMany(id => catalog.Budgets[(BudgetScope.VirtualKey, id)]),
            .. catalog.Budgets[(BudgetScope.Team, key.TeamId)],
            .. catalog.Budgets[(BudgetScope.Department, departmentId)],
        ];
    }

    /// <summary>
    /// Reserves the estimate against every applicable budget in one ledger round trip. Counters missing from the
    /// shared store (cold start, new period) are seeded from the usage table and the reservation is retried.
    /// </summary>
    public async Task<BudgetReservation> ReserveAsync(IReadOnlyList<Budget> budgets, decimal estimatedSek, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.Count == 0)
        {
            return BudgetReservation.None;
        }

        var checks = BuildChecks(budgets);
        SpendCounter[] counters = [.. checks.Select(c => c.Counter)];
        var amount = Math.Max(0, ToMicro(estimatedSek));
        var result = await ledger.ReserveAsync(counters, amount, missingAsZero: false, cancellationToken);
        if (result.Missing.Count > 0)
        {
            await SeedAsync([.. result.Missing.Select(i => checks[i])], cancellationToken);
            // A counter can only vanish again if it expired in between; count it from zero rather than loop.
            result = await ledger.ReserveAsync(counters, amount, missingAsZero: true, cancellationToken);
        }

        var remaining = checks.Select((c, i) => (c.Counter.LimitMicroSek - result.ValuesBefore[i]) / (decimal)MicroPerSek).Min();
        return result.ExhaustedIndex >= 0
            ? new BudgetReservation(checks, 0, checks[result.ExhaustedIndex], Math.Max(0, remaining))
            : new BudgetReservation(checks, amount, null, Math.Max(0, remaining));
    }

    /// <summary>
    /// Read-only: how full the applicable budgets are, as the highest utilisation (0-100) over all of them, i.e. the
    /// budget closest to rejecting requests. Null when no budget applies. Reserves and changes nothing.
    /// </summary>
    public async Task<double?> PeekUsedPercentAsync(IReadOnlyList<Budget> budgets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.Count == 0)
        {
            return null;
        }

        var checks = BuildChecks(budgets);
        await SeedMissingCountersAsync(checks, cancellationToken);
        var values = await ledger.GetAsync([.. checks.Select(c => c.Counter.Key)], cancellationToken);
        return checks.Select((c, i) => BudgetEvaluator.UtilisationPercent((values[i] ?? 0) / (decimal)MicroPerSek, c.Budget.LimitSek)).Max();
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

    private List<BudgetCheck> BuildChecks(IReadOnlyList<Budget> budgets)
    {
        var now = time.GetUtcNow();
        return [.. budgets.Select(b =>
        {
            var window = BudgetPeriods.GetWindow(b.Period, now);
            var key = SpendCounter.KeyFor(b.Scope, b.ScopeId, b.Period, window.Start);
            return new BudgetCheck(b, window, new SpendCounter(key, ToMicro(b.LimitSek), window.End - now + TimeSpan.FromDays(1)));
        })];
    }

    /// <summary>Seeds counters missing from the shared store (cold start / new period) from the usage table.</summary>
    private async Task SeedMissingCountersAsync(List<BudgetCheck> checks, CancellationToken cancellationToken)
    {
        var values = await ledger.GetAsync([.. checks.Select(c => c.Counter.Key)], cancellationToken);
        await SeedAsync([.. checks.Where((_, i) => values[i] is null)], cancellationToken);
    }

    private async Task SeedAsync(IReadOnlyList<BudgetCheck> missing, CancellationToken cancellationToken)
    {
        foreach (var check in missing)
        {
            var key = check.Counter.Key;
            var seeding = _seeding.GetOrAdd(key, k => new Lazy<Task>(async () =>
            {
                try
                {
                    // Not tied to one request: concurrent requests for the same counter share this work.
                    var spent = await SpentFromDatabaseAsync(check.Budget, check.Window, CancellationToken.None);
                    await ledger.InitializeAsync(key, ToMicro(spent), check.Counter.TimeToLive, CancellationToken.None);
                }
                finally
                {
                    _seeding.TryRemove(key, out _);
                }
            }));
            await seeding.Value.WaitAsync(cancellationToken);
        }
    }

    private async Task<decimal> SpentFromDatabaseAsync(Budget budget, PeriodWindow window, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().SpentAsync(budget, window, cancellationToken);
    }

    public static long ToMicro(decimal sek) => (long)decimal.Round(sek * MicroPerSek, MidpointRounding.AwayFromZero);
}
