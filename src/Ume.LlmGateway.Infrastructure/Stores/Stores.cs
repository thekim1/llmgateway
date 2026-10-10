using System.Collections.Concurrent;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Stores;

public readonly record struct RateLimitDecision(bool Allowed, int? RequestLimit, int? RequestsRemaining, TimeSpan RetryAfter, string? Reason)
{
    public static RateLimitDecision Unlimited { get; } = new(true, null, null, TimeSpan.Zero, null);
}

/// <summary>
/// Per virtual key fixed-window (1 minute) limits on requests and tokens. Like every store here, the Redis
/// implementation is bounded by the multiplexer's timeouts and does not observe cancellation tokens (see
/// <see cref="RedisRateLimiter"/>); in-memory implementations complete synchronously.
/// </summary>
public interface IRateLimiter
{
    Task<RateLimitDecision> AcquireAsync(Guid keyId, int? requestsPerMinute, int? tokensPerMinute, CancellationToken cancellationToken);
    Task RecordTokensAsync(Guid keyId, long tokens, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only: percent (0-100) of the key's tokens-per-minute limit used in the current minute, or null when the key
    /// has no token limit. Does not count as a request.
    /// </summary>
    Task<double?> PeekTokensUsedPercentAsync(Guid keyId, int? tokensPerMinute, CancellationToken cancellationToken);
}

/// <summary>A spend counter for one budget scope and period window, in micro-SEK (1 SEK = 1 000 000).</summary>
public sealed record SpendCounter(string Key, long LimitMicroSek, TimeSpan TimeToLive)
{
    public static string KeyFor(BudgetScope scope, Guid scopeId, BudgetPeriod period, DateTimeOffset periodStart) =>
        RedisKeys.Spend(scope, scopeId, period, periodStart);
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
    Task InitializeAsync(string key, long valueMicroSek, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically, in one round trip, checks every counter is below its limit with room for the amount and adds it, and
    /// returns each counter's value before the reservation. Unless <paramref name="missingAsZero"/> is set, nothing is
    /// reserved when a counter is missing: the result lists the missing counters so the caller can seed them and try again.
    /// </summary>
    Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroSek, bool missingAsZero, CancellationToken cancellationToken);

    /// <summary>Adds (possibly negative) delta to all counters; returns the new values.</summary>
    Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, long deltaMicroSek, CancellationToken cancellationToken);
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

/// <summary>Shared per-provider circuit breaker state so all gateway instances and the admin API agree.</summary>
public interface ICircuitBreakerStore
{
    Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken);
    Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken);
    Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken);
    Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken);
}

public sealed class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan SamplingWindow { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>The fixed one-minute window shared by the rate limiters.</summary>
internal static class RateLimitWindow
{
    /// <summary>The current minute (Unix minutes) and the time left until it ends.</summary>
    public static (long Minute, TimeSpan RetryAfter) Current(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        return (seconds / 60, TimeSpan.FromSeconds(60 - (seconds % 60)));
    }

    public static double TokenPercent(long used, int limit) => Math.Clamp(used * 100d / limit, 0d, 100d);
}

/// <summary>Single-instance implementations used in tests and when Redis is not configured.</summary>
public sealed class InMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private readonly ConcurrentDictionary<Counter, long> _counters = new();
    private readonly Lock _lock = new();
    private long _prunedMinute;

    private readonly record struct Counter(Guid KeyId, long Minute, bool Tokens);

    public Task<RateLimitDecision> AcquireAsync(Guid keyId, int? requestsPerMinute, int? tokensPerMinute, CancellationToken cancellationToken)
    {
        var (minute, retry) = Window();
        lock (_lock)
        {
            var tokens = _counters.GetValueOrDefault(new Counter(keyId, minute, Tokens: true));
            if (tokensPerMinute is > 0 && tokens >= tokensPerMinute)
            {
                return Task.FromResult(new RateLimitDecision(false, requestsPerMinute, 0, retry, "tokens"));
            }

            var requests = _counters.AddOrUpdate(new Counter(keyId, minute, Tokens: false), 1, (_, v) => v + 1);
            if (requestsPerMinute is > 0 && requests > requestsPerMinute)
            {
                return Task.FromResult(new RateLimitDecision(false, requestsPerMinute, 0, retry, "requests"));
            }

            int? remaining = requestsPerMinute is > 0 ? (int)Math.Max(0, requestsPerMinute.Value - requests) : null;
            return Task.FromResult(new RateLimitDecision(true, requestsPerMinute, remaining, TimeSpan.Zero, null));
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

        var (minute, _) = Window();
        return Task.FromResult<double?>(RateLimitWindow.TokenPercent(_counters.GetValueOrDefault(new Counter(keyId, minute, Tokens: true)), tokensPerMinute.Value));
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

    public Task InitializeAsync(string key, long valueMicroSek, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _values.TryAdd(key, valueMicroSek);
        }

        return Task.CompletedTask;
    }

    public Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroSek, bool missingAsZero, CancellationToken cancellationToken)
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
                if (before[i] >= counters[i].LimitMicroSek || amountMicroSek > counters[i].LimitMicroSek - before[i])
                {
                    return Task.FromResult(new SpendReservation(i, before, []));
                }
            }

            for (var i = 0; i < counters.Count; i++)
            {
                _values[counters[i].Key] = before[i] + amountMicroSek;
            }

            return Task.FromResult(new SpendReservation(-1, before, []));
        }
    }

    public Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, long deltaMicroSek, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<long> result = [.. counters.Select(c => _values[c.Key] = _values.GetValueOrDefault(c.Key) + deltaMicroSek)];
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

    public Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        IReadOnlySet<Guid> open = providerIds.Where(id => _openUntil.TryGetValue(id, out var until) && until > now).ToHashSet();
        return Task.FromResult(open);
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
