using System.Collections.Concurrent;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Infrastructure.Stores;

public readonly record struct RateLimitDecision(bool Allowed, int? RequestLimit, int? RequestsRemaining, TimeSpan RetryAfter, string? Reason)
{
    public static RateLimitDecision Unlimited { get; } = new(true, null, null, TimeSpan.Zero, null);
}

/// <summary>
/// Per virtual key limits on requests and tokens per minute and on requests in flight. The per-minute limits use a
/// sliding window: the current minute's count plus the previous minute's, weighted by how much of it the last 60 seconds
/// still cover, so a client cannot send twice its limit across a minute boundary. Like every store here, the Redis
/// implementation is bounded by the multiplexer's timeouts and does not observe cancellation tokens (see
/// <see cref="RedisRateLimiter"/>); in-memory implementations complete synchronously.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Counts a request against the per-minute limits and, with <paramref name="maxConcurrent"/> also takes an in-flight slot named <paramref name="leaseId"/>
    /// (refused with reason <c>concurrency</c> when all are taken). Release it with <see cref="Release"/>; a slot
    /// that is never released expires after <see cref="RateLimitWindow.InFlightLease"/>. Denied requests count nowhere.
    /// </summary>
    Task<RateLimitDecision> AcquireAsync(Guid keyId, int? requestsPerMinute, int? tokensPerMinute, int? maxConcurrent, string? leaseId, CancellationToken cancellationToken);

    /// <summary>Frees the in-flight slot taken by <see cref="AcquireAsync(Guid, int?, int?, int?, string?, CancellationToken)"/>. Fire-and-forget in Redis.</summary>
    void Release(Guid keyId, string leaseId);

    Task RecordTokensAsync(Guid keyId, long tokens, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only: percent (0-100) of the key's tokens-per-minute limit used in the current minute, or null when the key
    /// has no token limit. Does not count as a request.
    /// </summary>
    Task<double?> PeekTokensUsedPercentAsync(Guid keyId, int? tokensPerMinute, CancellationToken cancellationToken);
}

public static class RateLimiterExtensions
{
    /// <summary>The per-minute limits only, without an in-flight slot.</summary>
    public static Task<RateLimitDecision> AcquireAsync(this IRateLimiter limiter, Guid keyId, int? requestsPerMinute, int? tokensPerMinute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        return limiter.AcquireAsync(keyId, requestsPerMinute, tokensPerMinute, null, null, cancellationToken);
    }
}

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

/// <summary>
/// Open realtime (WebSocket) sessions per virtual key, shared by all gateway instances. A session holds its slot for
/// <c>timeToLive</c> and renews it while it runs, so slots of a crashed instance free themselves.
/// </summary>
public interface IRealtimeSessionRegistry
{
    /// <summary>Takes a slot unless the key already has <paramref name="maxSessions"/> open (0 = no limit).</summary>
    Task<bool> TryOpenAsync(Guid keyId, string sessionId, int maxSessions, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task RenewAsync(Guid keyId, string sessionId, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task CloseAsync(Guid keyId, string sessionId, CancellationToken cancellationToken);
}

public enum InvalidationKind
{
    Keys = 0,
    Config = 1,
}

/// <summary>
/// Signed-in admin sessions, shared by all admin API instances, so signing out ends a session everywhere and a copied cookie
/// stops working. Values are opaque (the admin API encrypts them); they expire on their own.
/// </summary>
public interface IAdminSessionStore
{
    Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Cross-instance cache invalidation (key revoked, config changed). Redis pub/sub in production.</summary>
public interface IInvalidationBus
{
    Task PublishAsync(InvalidationKind kind, CancellationToken cancellationToken);
    IDisposable Subscribe(Action<InvalidationKind> handler);
}

public enum CircuitState
{
    Closed = 0,
    Open = 1,
}

/// <summary>Providers whose circuit is open and model deployments on cooldown, from one lookup.</summary>
public sealed record CircuitStatus(IReadOnlySet<Guid> OpenProviders, IReadOnlySet<Guid> CoolingDeployments)
{
    public static CircuitStatus None { get; } = new(new HashSet<Guid>(), new HashSet<Guid>());

    /// <summary>The provider's circuit is open or the deployment is on cooldown: try it only after everything else.</summary>
    public bool IsUnavailable(Guid providerId, Guid deploymentId) => OpenProviders.Contains(providerId) || CoolingDeployments.Contains(deploymentId);
}

/// <summary>
/// Shared per-provider circuit breaker state and per-deployment cooldowns, so all gateway instances and the admin API
/// agree. A cooldown is what a provider asked for with <c>Retry-After</c> (a quota, not a health problem), so it
/// applies to the one deployment and does not count against the provider's circuit.
/// </summary>
public interface ICircuitBreakerStore
{
    Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken);

    /// <summary>Open circuits of <paramref name="providerIds"/> and cooldowns of <paramref name="deploymentIds"/> in one round trip.</summary>
    Task<CircuitStatus> GetStatusAsync(IReadOnlyCollection<Guid> providerIds, IReadOnlyCollection<Guid> deploymentIds, CancellationToken cancellationToken);

    Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>Takes the deployment out of rotation for <paramref name="duration"/>; an existing longer cooldown is kept.</summary>
    Task CoolDownAsync(Guid deploymentId, TimeSpan duration, CancellationToken cancellationToken);

    Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken);
    Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken);
}

public sealed class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan SamplingWindow { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest cooldown a provider's <c>Retry-After</c> can put a deployment on; zero ignores <c>Retry-After</c>.</summary>
    [System.ComponentModel.DataAnnotations.Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>The one-minute windows shared by the rate limiters, and the sliding estimate over the last two.</summary>
public static class RateLimitWindow
{
    /// <summary>
    /// How long an in-flight slot lasts without being released: the longest provider timeout. A stream that lives
    /// longer stops counting toward the concurrency limit after this.
    /// </summary>
    public static readonly TimeSpan InFlightLease = TimeSpan.FromSeconds(900);

    /// <summary>The current minute (Unix minutes) and the time left until it ends.</summary>
    public static (long Minute, TimeSpan RetryAfter) Current(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        return (seconds / 60, TimeSpan.FromSeconds(60 - (seconds % 60)));
    }

    /// <summary>The current minute and how far into it <paramref name="now"/> is (0 to 1).</summary>
    public static (long Minute, double Elapsed) Sliding(DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();
        return (ms / 60_000, ms % 60_000 / 60_000d);
    }

    /// <summary>The sliding count: all of this minute plus the part of the previous one the last 60 seconds cover.</summary>
    public static double Estimate(long previous, long current, double elapsed) => current + previous * (1 - elapsed);

    /// <summary>
    /// How long until <paramref name="cost"/> more fits under <paramref name="limit"/>, as the previous minute's weight
    /// falls (and, if this minute alone is too much, as it in turn becomes the previous one). At least one second.
    /// </summary>
    public static TimeSpan RetryAfter(long previous, long current, long cost, long limit, double elapsed)
    {
        double wait;
        if (current + cost <= limit && previous > 0)
        {
            // This minute: previous × (1 − f) + current + cost ≤ limit once f reaches the needed fraction.
            wait = 1 - (double)(limit - current - cost) / previous - elapsed;
        }
        else
        {
            // Next minute, when this minute's count is the one that fades.
            var next = current > 0 ? Math.Max(0, 1 - (double)(limit - cost) / current) : 0;
            wait = 1 - elapsed + next;
        }

        return TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(wait * 60)));
    }

    public static double TokenPercent(double used, int limit) => Math.Clamp(used * 100d / limit, 0d, 100d);
}

/// <summary>Single-instance implementations used in tests and when Redis is not configured.</summary>
public sealed class InMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private readonly ConcurrentDictionary<Counter, long> _counters = new();
    private readonly Lock _lock = new();
    private long _prunedMinute;

    private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _inFlight = [];

    private readonly record struct Counter(Guid KeyId, long Minute, bool Tokens);

    public Task<RateLimitDecision> AcquireAsync(Guid keyId, int? requestsPerMinute, int? tokensPerMinute, int? maxConcurrent, string? leaseId,
        CancellationToken cancellationToken)
    {
        Window();
        var now = time.GetUtcNow();
        var (minute, elapsed) = RateLimitWindow.Sliding(now);
        lock (_lock)
        {
            var tokens = _counters.GetValueOrDefault(new Counter(keyId, minute, Tokens: true));
            var previousTokens = _counters.GetValueOrDefault(new Counter(keyId, minute - 1, Tokens: true));
            if (tokensPerMinute is > 0 && RateLimitWindow.Estimate(previousTokens, tokens, elapsed) >= tokensPerMinute)
            {
                return Task.FromResult(new RateLimitDecision(false, requestsPerMinute, 0,
                    RateLimitWindow.RetryAfter(previousTokens, tokens, 1, tokensPerMinute.Value, elapsed), "tokens"));
            }

            var requests = _counters.GetValueOrDefault(new Counter(keyId, minute, Tokens: false));
            var previousRequests = _counters.GetValueOrDefault(new Counter(keyId, minute - 1, Tokens: false));
            if (requestsPerMinute is > 0 && RateLimitWindow.Estimate(previousRequests, requests, elapsed) + 1 > requestsPerMinute)
            {
                return Task.FromResult(new RateLimitDecision(false, requestsPerMinute, 0,
                    RateLimitWindow.RetryAfter(previousRequests, requests, 1, requestsPerMinute.Value, elapsed), "requests"));
            }

            if (maxConcurrent is > 0 && leaseId is not null)
            {
                if (!_inFlight.TryGetValue(keyId, out var leases))
                {
                    _inFlight[keyId] = leases = [];
                }

                foreach (var expired in leases.Where(l => l.Value <= now).Select(l => l.Key).ToList())
                {
                    leases.Remove(expired);
                }

                if (leases.Count >= maxConcurrent)
                {
                    return Task.FromResult(new RateLimitDecision(false, requestsPerMinute, null, TimeSpan.FromSeconds(1), "concurrency"));
                }

                leases[leaseId] = now + RateLimitWindow.InFlightLease;
            }

            requests = _counters.AddOrUpdate(new Counter(keyId, minute, Tokens: false), 1, (_, v) => v + 1);
            int? remaining = requestsPerMinute is > 0
                ? (int)Math.Max(0, requestsPerMinute.Value - Math.Ceiling(RateLimitWindow.Estimate(previousRequests, requests, elapsed)))
                : null;
            return Task.FromResult(new RateLimitDecision(true, requestsPerMinute, remaining, TimeSpan.Zero, null));
        }
    }

    public void Release(Guid keyId, string leaseId)
    {
        lock (_lock)
        {
            if (_inFlight.TryGetValue(keyId, out var leases) && leases.Remove(leaseId) && leases.Count == 0)
            {
                _inFlight.Remove(keyId);
            }
        }
    }

    public Task RecordTokensAsync(Guid keyId, long tokens, CancellationToken cancellationToken)
    {
        var (minute, _) = Window();
        _counters.AddOrUpdate(new Counter(keyId, minute, Tokens: true), tokens, (_, v) => v + tokens);
        return Task.CompletedTask;
    }

    public Task<double?> PeekTokensUsedPercentAsync(Guid keyId, int? tokensPerMinute, CancellationToken cancellationToken)
    {
        if (tokensPerMinute is not > 0)
        {
            return Task.FromResult<double?>(null);
        }

        Window();
        var (minute, elapsed) = RateLimitWindow.Sliding(time.GetUtcNow());
        var used = RateLimitWindow.Estimate(_counters.GetValueOrDefault(new Counter(keyId, minute - 1, Tokens: true)),
            _counters.GetValueOrDefault(new Counter(keyId, minute, Tokens: true)), elapsed);
        return Task.FromResult<double?>(RateLimitWindow.TokenPercent(used, tokensPerMinute.Value));
    }

    /// <summary>Number of counters held (tests: old windows are dropped).</summary>
    internal int Count => _counters.Count;

    /// <summary>The current window; the first call in a new minute drops the counters of earlier ones.</summary>
    private (long Minute, TimeSpan RetryAfter) Window()
    {
        var window = RateLimitWindow.Current(time.GetUtcNow());
        if (Interlocked.Read(ref _prunedMinute) != window.Minute && Interlocked.Exchange(ref _prunedMinute, window.Minute) != window.Minute)
        {
            foreach (var counter in _counters.Keys)
            {
                // The previous minute is kept: tokens of a request that started in it may still be reported.
                if (counter.Minute < window.Minute - 1)
                {
                    _counters.TryRemove(counter, out _);
                }
            }
        }

        return window;
    }
}

public sealed class InMemoryRealtimeSessionRegistry(TimeProvider time) : IRealtimeSessionRegistry
{
    private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _sessions = [];
    private readonly Lock _lock = new();

    public Task<bool> TryOpenAsync(Guid keyId, string sessionId, int maxSessions, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (!_sessions.TryGetValue(keyId, out var open))
            {
                _sessions[keyId] = open = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            }

            foreach (var expired in open.Where(s => s.Value <= now).Select(s => s.Key).ToList())
            {
                open.Remove(expired);
            }

            if (maxSessions > 0 && open.Count >= maxSessions)
            {
                return Task.FromResult(false);
            }

            open[sessionId] = now + timeToLive;
            return Task.FromResult(true);
        }
    }

    public Task RenewAsync(Guid keyId, string sessionId, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(keyId, out var open) && open.ContainsKey(sessionId))
            {
                open[sessionId] = time.GetUtcNow() + timeToLive;
            }
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(Guid keyId, string sessionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(keyId, out var open) && open.Remove(sessionId) && open.Count == 0)
            {
                _sessions.Remove(keyId);
            }
        }

        return Task.CompletedTask;
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

public sealed class InMemoryInvalidationBus : IInvalidationBus
{
    private readonly List<Action<InvalidationKind>> _handlers = [];
    private readonly Lock _lock = new();

    public Task PublishAsync(InvalidationKind kind, CancellationToken cancellationToken)
    {
        Action<InvalidationKind>[] handlers;
        lock (_lock)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            handler(kind);
        }

        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Action<InvalidationKind> handler)
    {
        lock (_lock)
        {
            _handlers.Add(handler);
        }

        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                _handlers.Remove(handler);
            }
        });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class InMemoryCircuitBreakerStore(TimeProvider time, Microsoft.Extensions.Options.IOptions<CircuitBreakerOptions> options) : ICircuitBreakerStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _openUntil = new();
    private readonly ConcurrentDictionary<Guid, List<DateTimeOffset>> _failures = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _coolUntil = new();

    public Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        IReadOnlySet<Guid> open = providerIds.Where(id => _openUntil.TryGetValue(id, out var until) && until > now).ToHashSet();
        return Task.FromResult(open);
    }

    public async Task<CircuitStatus> GetStatusAsync(IReadOnlyCollection<Guid> providerIds, IReadOnlyCollection<Guid> deploymentIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var open = await GetOpenAsync(providerIds, cancellationToken);
        var cooling = deploymentIds.Where(id => _coolUntil.TryGetValue(id, out var until) && until > now).ToHashSet();
        return open.Count == 0 && cooling.Count == 0 ? CircuitStatus.None : new CircuitStatus(open, cooling);
    }

    public Task CoolDownAsync(Guid deploymentId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + duration;
        _coolUntil.AddOrUpdate(deploymentId, until, (_, existing) => existing > until ? existing : until);
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var o = options.Value;
        var list = _failures.GetOrAdd(providerId, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t < now - o.SamplingWindow);
            list.Add(now);
            if (list.Count >= o.FailureThreshold)
            {
                _openUntil[providerId] = now + o.BreakDuration;
                list.Clear();
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken)
    {
        if (_failures.TryGetValue(providerId, out var list))
        {
            lock (list)
            {
                list.Clear();
            }
        }

        return Task.CompletedTask;
    }

    public Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken)
    {
        if (state == CircuitState.Open)
        {
            _openUntil[providerId] = time.GetUtcNow() + (openFor ?? options.Value.BreakDuration);
        }
        else
        {
            _openUntil.TryRemove(providerId, out _);
            _failures.TryRemove(providerId, out _);
        }

        return Task.CompletedTask;
    }
}

public sealed class InMemoryAdminSessionStore(TimeProvider time) : IAdminSessionStore
{
    private readonly ConcurrentDictionary<string, (byte[] Value, DateTimeOffset Expires)> _sessions = new(StringComparer.Ordinal);

    public Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        _sessions[sessionId] = (value, time.GetUtcNow() + timeToLive);
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Expires > time.GetUtcNow())
        {
            return Task.FromResult<byte[]?>(session.Value);
        }

        _sessions.TryRemove(sessionId, out _);
        return Task.FromResult<byte[]?>(null);
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}
