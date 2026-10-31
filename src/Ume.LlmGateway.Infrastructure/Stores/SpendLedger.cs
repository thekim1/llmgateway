using StackExchange.Redis;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>
/// A spend counter for one budget and period window, with its limit: micro-USD (1 USD = 1 000 000) for cost budgets,
/// tokens for token budgets.
/// </summary>
public sealed record SpendCounter(string Key, long Limit, TimeSpan TimeToLive)
{
    public static string KeyFor(BudgetScope scope, Guid scopeId, BudgetPeriod period, DateTimeOffset periodStart) =>
        RedisKeys.Spend(scope, scopeId, period, periodStart);

    /// <summary>The counter of a budget: cost budgets by owner and period, token budgets also by the residency they count.</summary>
    public static string KeyFor(Budget budget, DateTimeOffset periodStart)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return budget.Unit == BudgetUnit.Tokens
            ? RedisKeys.TokenSpend(budget.Scope, budget.ScopeId, budget.Period, periodStart, budget.Residency)
            : RedisKeys.Spend(budget.Scope, budget.ScopeId, budget.Period, periodStart);
    }
}

/// <summary>
/// Outcome of <see cref="ISpendLedger.ReserveAsync"/>. <c>ExhaustedIndex</c> is -1 when the amount was reserved.
/// <c>ValuesBefore</c> holds every counter's value before this reservation (empty when counters are missing).
/// </summary>
public sealed record SpendReservation(int ExhaustedIndex, IReadOnlyList<long> ValuesBefore, IReadOnlyList<int> Missing)
{
    public bool Reserved => ExhaustedIndex < 0 && Missing.Count == 0;
}

/// <summary>
/// Shared spend counters used for budget enforcement. Counters include in-flight reservations;
/// reservations are reconciled to actual cost after the provider call. The Redis implementation is bounded by the
/// multiplexer's timeouts rather than the cancellation token.
/// </summary>
public interface ISpendLedger
{
    /// <summary>Returns the current value per key, or null if the key is missing (needs seeding from Postgres).</summary>
    Task<IReadOnlyList<long?>> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>Seeds a missing counter. No-op if it already exists (another instance won the race).</summary>
    Task InitializeAsync(string key, long valueMicroUsd, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically, in one round trip, checks every counter is below its limit with room for its amount (one per counter:
    /// micro-USD or tokens) and adds it, and returns each counter's value before the reservation. Unless
    /// <paramref name="missingAsZero"/> is set, nothing is reserved when a counter is missing: the result lists the
    /// missing counters so the caller can seed them and try again.
    /// </summary>
    Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, bool missingAsZero, CancellationToken cancellationToken);

    /// <summary>Adds each (possibly negative) delta to its counter; returns the new values.</summary>
    Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> deltas, CancellationToken cancellationToken);
}

public static class SpendLedgerExtensions
{
    /// <summary>The same amount reserved against every counter.</summary>
    public static Task<SpendReservation> ReserveAsync(this ISpendLedger ledger, IReadOnlyList<SpendCounter> counters, long amount, bool missingAsZero,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(counters);
        return ledger.ReserveAsync(counters, [.. Enumerable.Repeat(amount, counters.Count)], missingAsZero, cancellationToken);
    }

    /// <summary>The same delta added to every counter.</summary>
    public static Task<IReadOnlyList<long>> AddAsync(this ISpendLedger ledger, IReadOnlyList<SpendCounter> counters, long delta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(counters);
        return ledger.AddAsync(counters, [.. Enumerable.Repeat(delta, counters.Count)], cancellationToken);
    }
}

public sealed class InMemorySpendLedger : ISpendLedger
{
    private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public Task<IReadOnlyList<long?>> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<long?> result = [.. keys.Select(k => _values.TryGetValue(k, out var v) ? v : (long?)null)];
            return Task.FromResult(result);
        }
    }

    public Task InitializeAsync(string key, long valueMicroUsd, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _values.TryAdd(key, valueMicroUsd);
        }

        return Task.CompletedTask;
    }

    public Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, bool missingAsZero, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            List<int> missing = missingAsZero ? [] : [.. counters.Select((c, i) => (c, i)).Where(x => !_values.ContainsKey(x.c.Key)).Select(x => x.i)];
            if (missing.Count > 0)
            {
                return Task.FromResult(new SpendReservation(-1, [], missing));
            }

            var before = counters.Select(c => _values.GetValueOrDefault(c.Key)).ToArray();
            for (var i = 0; i < counters.Count; i++)
            {
                if (before[i] >= counters[i].Limit || amounts[i] > counters[i].Limit - before[i])
                {
                    return Task.FromResult(new SpendReservation(i, before, []));
                }
            }

            for (var i = 0; i < counters.Count; i++)
            {
                _values[counters[i].Key] = before[i] + amounts[i];
            }

            return Task.FromResult(new SpendReservation(-1, before, []));
        }
    }

    public Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> deltas, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<long> result = [.. counters.Select((c, i) => _values[c.Key] = _values.GetValueOrDefault(c.Key) + deltas[i])];
            return Task.FromResult(result);
        }
    }
}

public sealed class RedisSpendLedger(IConnectionMultiplexer redis) : ISpendLedger
{
    // ARGV: missing-as-zero, then per counter: amount, limit, TTL (s).
    // Returns {-2, missing indexes} | {exhausted index, values before} | {-1, values before}.
    private const string ReserveScript = """
        local values = redis.call('MGET', unpack(KEYS))
        local missing = {}
        for i = 1, #KEYS do
          if not values[i] then
            if ARGV[1] == '1' then values[i] = 0 else missing[#missing + 1] = i - 1 end
          else
            values[i] = tonumber(values[i])
          end
        end
        if #missing > 0 then return {-2, missing} end
        for i = 1, #KEYS do
          local amount = tonumber(ARGV[i * 3 - 1])
          local limit = tonumber(ARGV[i * 3])
          if values[i] >= limit or amount > limit - values[i] then return {i - 1, values} end
        end
        for i = 1, #KEYS do
          redis.call('INCRBY', KEYS[i], ARGV[i * 3 - 1])
          redis.call('EXPIRE', KEYS[i], ARGV[i * 3 + 1])
        end
        return {-1, values}
        """;

    public async Task<IReadOnlyList<long?>> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var values = await redis.GetDatabase().StringGetAsync([.. keys.Select(k => (RedisKey)k)]);
        return [.. values.Select(v => v.HasValue ? (long?)(long)v : null)];
    }

    public Task InitializeAsync(string key, long valueMicroUsd, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(key, valueMicroUsd, timeToLive, When.NotExists);

    public async Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, bool missingAsZero, CancellationToken cancellationToken)
    {
        if (counters.Count == 0)
        {
            return new SpendReservation(-1, [], []);
        }

        var args = new RedisValue[1 + (counters.Count * 3)];
        args[0] = missingAsZero ? 1 : 0;
        for (var i = 0; i < counters.Count; i++)
        {
            args[1 + (i * 3)] = amounts[i];
            args[2 + (i * 3)] = counters[i].Limit;
            args[3 + (i * 3)] = (long)Math.Max(60, counters[i].TimeToLive.TotalSeconds);
        }

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(ReserveScript, [.. counters.Select(c => (RedisKey)c.Key)], args))!;
        var status = (int)result[0];
        var items = (RedisResult[])result[1]!;
        return status == -2
            ? new SpendReservation(-1, [], [.. items.Select(r => (int)r)])
            : new SpendReservation(status, [.. items.Select(r => (long)r)], []);
    }

    public async Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> deltas, CancellationToken cancellationToken)
    {
        // Issued together so they are pipelined: one round trip however many budgets apply.
        var db = redis.GetDatabase();
        return await Task.WhenAll(counters.Select((c, i) => db.StringIncrementAsync(c.Key, deltas[i])));
    }
}
