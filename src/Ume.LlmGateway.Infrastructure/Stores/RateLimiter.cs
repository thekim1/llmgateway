using System.Collections.Concurrent;
using StackExchange.Redis;

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

/// <summary>
/// Redis-backed stores. Only counters, circuit flags, invalidation signals and encrypted admin sessions are stored – never
/// prompt or response content, never key material. All keys are namespaced with <c>ume:</c> (see <see cref="RedisKeys"/>).
/// Calls are bounded by the multiplexer's sync/async timeouts; cancellation tokens are not passed on (StackExchange.Redis
/// cannot cancel a command once it is queued, and wrapping every call would cost an allocation on the request path).
/// </summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, TimeProvider time) : IRateLimiter
{
    // KEYS: requests this minute, tokens this minute, requests last minute, tokens last minute, in-flight set.
    // ARGV: rpm, tpm, counter TTL (s), weight of last minute, max in flight, now (ms), lease expiry (ms), lease id, set TTL (ms).
    // Returns {status, this minute's count, last minute's count}: status -1 requests, -2 tokens, -3 in flight, else the
    // request count including this one. Only an admitted request is counted.
    private const string Script = """
        local w = tonumber(ARGV[4])
        local tpm = tonumber(ARGV[2])
        if tpm > 0 then
          local tok = tonumber(redis.call('GET', KEYS[2]) or '0')
          local prevTok = tonumber(redis.call('GET', KEYS[4]) or '0')
          if tok + prevTok * w >= tpm then return {-2, tok, prevTok} end
        end
        local rpm = tonumber(ARGV[1])
        local prevReq = 0
        if rpm > 0 then
          local req = tonumber(redis.call('GET', KEYS[1]) or '0')
          prevReq = tonumber(redis.call('GET', KEYS[3]) or '0')
          if req + prevReq * w + 1 > rpm then return {-1, req, prevReq} end
        end
        local max = tonumber(ARGV[5])
        if max > 0 then
          redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', ARGV[6])
          if redis.call('ZCARD', KEYS[5]) >= max then return {-3, 0, 0} end
          redis.call('ZADD', KEYS[5], ARGV[7], ARGV[8])
          redis.call('PEXPIRE', KEYS[5], ARGV[9])
        end
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], ARGV[3]) end
        return {n, n, prevReq}
        """;

    public async Task<RateLimitDecision> AcquireAsync(Guid keyId, int? requestsPerMinute, int? tokensPerMinute, int? maxConcurrent, string? leaseId,
        CancellationToken cancellationToken)
    {
        var concurrency = maxConcurrent is > 0 && leaseId is not null ? maxConcurrent.Value : 0;
        if (requestsPerMinute is not > 0 && tokensPerMinute is not > 0 && concurrency == 0)
        {
            return RateLimitDecision.Unlimited;
        }

        var now = time.GetUtcNow();
        var (minute, elapsed) = RateLimitWindow.Sliding(now);
        var nowMs = now.ToUnixTimeMilliseconds();
        var lease = (long)RateLimitWindow.InFlightLease.TotalMilliseconds;
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [RedisKeys.RateRequests(keyId, minute), RedisKeys.RateTokens(keyId, minute), RedisKeys.RateRequests(keyId, minute - 1),
             RedisKeys.RateTokens(keyId, minute - 1), RedisKeys.InFlight(keyId)],
            [requestsPerMinute ?? 0, tokensPerMinute ?? 0, RedisKeys.RateWindowSeconds, 1 - elapsed, concurrency, nowMs, nowMs + lease,
             leaseId ?? string.Empty, lease]))!;

        var (status, current, previous) = ((long)result[0], (long)result[1], (long)result[2]);
        return status switch
        {
            -3 => new RateLimitDecision(false, requestsPerMinute, null, TimeSpan.FromSeconds(1), "concurrency"),
            -2 => new RateLimitDecision(false, requestsPerMinute, 0, RateLimitWindow.RetryAfter(previous, current, 1, tokensPerMinute!.Value, elapsed), "tokens"),
            -1 => new RateLimitDecision(false, requestsPerMinute, 0, RateLimitWindow.RetryAfter(previous, current, 1, requestsPerMinute!.Value, elapsed), "requests"),
            _ => new RateLimitDecision(true, requestsPerMinute,
                requestsPerMinute is > 0 ? (int)Math.Max(0, requestsPerMinute.Value - Math.Ceiling(RateLimitWindow.Estimate(previous, current, elapsed))) : null,
                TimeSpan.Zero, null),
        };
    }

    public void Release(Guid keyId, string leaseId) =>
        redis.GetDatabase().SortedSetRemove(RedisKeys.InFlight(keyId), leaseId, CommandFlags.FireAndForget);

    public async Task RecordTokensAsync(Guid keyId, long tokens, CancellationToken cancellationToken)
    {
        if (tokens <= 0)
        {
            return;
        }

        var (minute, _) = RateLimitWindow.Current(time.GetUtcNow());
        RedisKey key = RedisKeys.RateTokens(keyId, minute);
        var db = redis.GetDatabase();
        var increment = db.StringIncrementAsync(key, tokens);
        _ = db.KeyExpireAsync(key, RedisKeys.RateWindow, CommandFlags.FireAndForget); // pipelined after the INCRBY
        await increment;
    }

    public async Task<double?> PeekTokensUsedPercentAsync(Guid keyId, int? tokensPerMinute, CancellationToken cancellationToken)
    {
        if (tokensPerMinute is not > 0)
        {
            return null;
        }

        var (minute, elapsed) = RateLimitWindow.Sliding(time.GetUtcNow());
        var used = await redis.GetDatabase().StringGetAsync([RedisKeys.RateTokens(keyId, minute), RedisKeys.RateTokens(keyId, minute - 1)]);
        return RateLimitWindow.TokenPercent(
            RateLimitWindow.Estimate(used[1].HasValue ? (long)used[1] : 0, used[0].HasValue ? (long)used[0] : 0, elapsed), tokensPerMinute.Value);
    }
}
