using System.Collections.Concurrent;
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

    public BudgetReservation(IReadOnlyList<BudgetCheck> checks, long reservedMicroUsd, BudgetCheck? exceeded, decimal? remainingUsd, long reservedTokens = 0)
    {
        Checks = checks;
        ReservedMicroUsd = reservedMicroUsd;
        Exceeded = exceeded;
        RemainingUsd = remainingUsd;
        ReservedTokens = reservedTokens;
    }

    public IReadOnlyList<BudgetCheck> Checks { get; }
    public long ReservedMicroUsd { get; }

    /// <summary>Tokens reserved against each token budget among <see cref="Checks"/>.</summary>
    public long ReservedTokens { get; }
    public BudgetCheck? Exceeded { get; }

    /// <summary>Smallest remaining amount in USD over the applicable USD budgets (before this request), or null if there are none.</summary>
    public decimal? RemainingUsd { get; }

    public bool Allowed => Exceeded is null;
}

/// <summary>A crossed alert threshold. <c>SpentUsd</c> is the budget's use in its unit: USD, or tokens for a token budget.</summary>
public sealed record AlertCandidate(Budget Budget, PeriodWindow Window, int ThresholdPercent, decimal SpentUsd);

/// <summary>
/// Hierarchical budget enforcement (key → team → förvaltning). Uses shared counters with
/// reserve-then-reconcile so concurrent requests cannot all slip past a nearly exhausted budget. Cost budgets count USD,
/// the currency provider prices are in, so enforcement does not move with exchange rates (counters in micro-USD).
/// Token budgets count input plus output tokens, optionally only of providers of one residency; they apply to a request
/// when one of its candidate providers matches, and are charged only when the provider that served it does. Both kinds
/// are reserved in the same ledger round trip.
/// </summary>
public sealed class BudgetService(ISpendLedger ledger, IServiceScopeFactory scopes, TimeProvider time)
{
    public const long MicroPerUsd = 1_000_000;

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

    /// <summary>Whether any of <paramref name="budgets"/> counts tokens.</summary>
    public static bool HasTokenQuota(IReadOnlyList<Budget> budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        for (var i = 0; i < budgets.Count; i++)
        {
            if (budgets[i].Unit == BudgetUnit.Tokens)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reserves a cost estimate; token budgets are checked without reserving tokens.</summary>
    public Task<BudgetReservation> ReserveAsync(IReadOnlyList<Budget> budgets, decimal estimatedUsd, CancellationToken cancellationToken) =>
        ReserveAsync(budgets, estimatedUsd, 0, null, cancellationToken);

    /// <summary>
    /// Reserves the estimates against every applicable budget in one ledger round trip: <paramref name="estimatedUsd"/>
    /// against cost budgets, <paramref name="estimatedTokens"/> against token budgets. A token budget limited to a
    /// residency applies only when one of <paramref name="residencies"/> (the candidate providers') matches; null = all
    /// apply. Counters missing from the shared store (cold start, new period) are seeded from the usage table and the
    /// reservation is retried.
    /// </summary>
    public async Task<BudgetReservation> ReserveAsync(IReadOnlyList<Budget> budgets, decimal estimatedUsd, long estimatedTokens,
        IReadOnlyCollection<DataResidency>? residencies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        var applicable = residencies is null || !HasTokenQuota(budgets) ? budgets
            : [.. budgets.Where(b => b.Unit != BudgetUnit.Tokens || b.Residency is not { } r || residencies.Contains(r))];
        if (applicable.Count == 0)
        {
            return BudgetReservation.None;
        }

        var checks = BuildChecks(applicable);
        var micro = Math.Max(0, ToMicro(estimatedUsd));
        var tokens = Math.Max(0, estimatedTokens);
        var counters = new SpendCounter[checks.Count];
        var amounts = new long[checks.Count];
        for (var i = 0; i < checks.Count; i++)
        {
            counters[i] = checks[i].Counter;
            amounts[i] = checks[i].Budget.Unit == BudgetUnit.Tokens ? tokens : micro;
        }

        var result = await ledger.ReserveAsync(counters, amounts, missingAsZero: false, cancellationToken);
        if (result.Missing.Count > 0)
        {
            await SeedAsync([.. result.Missing.Select(i => checks[i])], cancellationToken);
            // A counter can only vanish again if it expired in between; count it from zero rather than loop.
            result = await ledger.ReserveAsync(counters, amounts, missingAsZero: true, cancellationToken);
        }

        decimal? remaining = null;
        for (var i = 0; i < checks.Count; i++)
        {
            if (checks[i].Budget.Unit != BudgetUnit.Tokens)
            {
                var left = Math.Max(0, (counters[i].Limit - result.ValuesBefore[i]) / (decimal)MicroPerUsd);
                remaining = remaining is { } r && r <= left ? r : left;
            }
        }
        return result.ExhaustedIndex >= 0
            ? new BudgetReservation(checks, 0, checks[result.ExhaustedIndex], remaining)
            : new BudgetReservation(checks, micro, null, remaining, tokens);
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
        return checks.Select((c, i) => BudgetEvaluator.UtilisationPercent(InUnit(c.Budget, values[i] ?? 0), c.Budget.Limit)).Max();
    }

    /// <summary>Replaces the reservation with the actual cost; token budgets get no tokens. Returns alert thresholds crossed.</summary>
    public Task<IReadOnlyList<AlertCandidate>> CommitAsync(BudgetReservation reservation, decimal actualUsd, CancellationToken cancellationToken) =>
        CommitAsync(reservation, actualUsd, 0, null, cancellationToken);

    /// <summary>
    /// Replaces the reservation with what the request used: <paramref name="actualUsd"/> on cost budgets, and
    /// <paramref name="actualTokens"/> on each token budget that counts <paramref name="servedBy"/> (the residency of the
    /// provider that served it; a request no provider served is charged no tokens). Returns alert thresholds crossed.
    /// </summary>
    public async Task<IReadOnlyList<AlertCandidate>> CommitAsync(BudgetReservation reservation, decimal actualUsd, long actualTokens,
        DataResidency? servedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.Checks.Count == 0 || !reservation.Allowed)
        {
            return [];
        }

        var actualMicro = ToMicro(actualUsd);
        var checks = reservation.Checks;
        var counters = new SpendCounter[checks.Count];
        var used = new long[checks.Count];
        var deltas = new long[checks.Count];
        for (var i = 0; i < checks.Count; i++)
        {
            var budget = checks[i].Budget;
            var tokens = budget.Unit == BudgetUnit.Tokens;
            counters[i] = checks[i].Counter;
            used[i] = !tokens ? actualMicro
                : servedBy is { } served && (budget.Residency is null || budget.Residency == served) ? Math.Max(0, actualTokens) : 0;
            deltas[i] = used[i] - (tokens ? reservation.ReservedTokens : reservation.ReservedMicroUsd);
        }

        var after = await ledger.AddAsync(counters, deltas, cancellationToken);
        var alerts = new List<AlertCandidate>();
        for (var i = 0; i < reservation.Checks.Count; i++)
        {
            var check = reservation.Checks[i];
            var afterValue = InUnit(check.Budget, after[i]);
            var beforeValue = afterValue - InUnit(check.Budget, used[i]);
            foreach (var threshold in BudgetEvaluator.CrossedThresholds(check.Budget, beforeValue, afterValue))
            {
                alerts.Add(new AlertCandidate(check.Budget, check.Window, threshold, afterValue));
            }
        }

        return alerts;
    }

    /// <summary>A counter value in the budget's unit: USD from micro-USD, tokens as they are.</summary>
    private static decimal InUnit(Budget budget, long counter) => budget.Unit == BudgetUnit.Tokens ? counter : counter / (decimal)MicroPerUsd;

    private List<BudgetCheck> BuildChecks(IReadOnlyList<Budget> budgets)
    {
        var now = time.GetUtcNow();
        return [.. budgets.Select(b =>
        {
            var window = BudgetPeriods.GetWindow(b.Period, now);
            var limit = b.Unit == BudgetUnit.Tokens ? b.LimitTokens : ToMicro(b.LimitUsd);
            return new BudgetCheck(b, window, new SpendCounter(SpendCounter.KeyFor(b, window.Start), limit, window.End - now + TimeSpan.FromDays(1)));
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
                    var value = check.Budget.Unit == BudgetUnit.Tokens ? (long)spent : ToMicro(spent);
                    await ledger.InitializeAsync(key, value, check.Counter.TimeToLive, CancellationToken.None);
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

    public static long ToMicro(decimal usd) => (long)decimal.Round(usd * MicroPerUsd, MidpointRounding.AwayFromZero);
}
