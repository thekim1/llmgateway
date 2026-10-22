using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>
/// Redis-backed stores. Only counters, circuit flags and invalidation signals are stored – never prompt or
/// response content, never key material. All keys are namespaced with <c>ume:</c> (see <see cref="RedisKeys"/>).
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

/// <summary>Open sessions per key as a sorted set of session id → expiry (ms). Expired members are dropped on open.</summary>
public sealed class RedisRealtimeSessionRegistry(IConnectionMultiplexer redis, TimeProvider time) : IRealtimeSessionRegistry
{
    private const string OpenScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        local max = tonumber(ARGV[3])
        if max > 0 and redis.call('ZCARD', KEYS[1]) >= max then return 0 end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return 1
        """;

    private const string RenewScript = """
        redis.call('ZADD', KEYS[1], 'XX', ARGV[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return 1
        """;

    public async Task<bool> TryOpenAsync(Guid keyId, string sessionId, int maxSessions, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var ttl = (long)timeToLive.TotalMilliseconds;
        var result = await redis.GetDatabase().ScriptEvaluateAsync(OpenScript, [Key(keyId)], [now, now + ttl, maxSessions, sessionId, ttl]);
        return (long)result == 1;
    }

    public Task RenewAsync(Guid keyId, string sessionId, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var ttl = (long)timeToLive.TotalMilliseconds;
        return redis.GetDatabase().ScriptEvaluateAsync(RenewScript, [Key(keyId)], [time.GetUtcNow().ToUnixTimeMilliseconds() + ttl, sessionId, ttl]);
    }

    public Task CloseAsync(Guid keyId, string sessionId, CancellationToken cancellationToken) =>
        redis.GetDatabase().SortedSetRemoveAsync(Key(keyId), sessionId);

    private static RedisKey Key(Guid keyId) => RedisKeys.RealtimeSessions(keyId);
}

public sealed class RedisSpendLedger(IConnectionMultiplexer redis) : ISpendLedger
{
    // Returns {-2, missing indexes} | {exhausted index, values before} | {-1, values before}.
    private const string ReserveScript = """
        local amount = tonumber(ARGV[1])
        local values = redis.call('MGET', unpack(KEYS))
        local missing = {}
        for i = 1, #KEYS do
          if not values[i] then
            if ARGV[2] == '1' then values[i] = 0 else missing[#missing + 1] = i - 1 end
          else
            values[i] = tonumber(values[i])
          end
        end
        if #missing > 0 then return {-2, missing} end
        for i = 1, #KEYS do
          local limit = tonumber(ARGV[i * 2 + 1])
          if values[i] >= limit or amount > limit - values[i] then return {i - 1, values} end
        end
        for i = 1, #KEYS do
          redis.call('INCRBY', KEYS[i], amount)
          redis.call('EXPIRE', KEYS[i], ARGV[i * 2 + 2])
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

    public async Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroUsd, bool missingAsZero, CancellationToken cancellationToken)
    {
        if (counters.Count == 0)
        {
            return new SpendReservation(-1, [], []);
        }

        var args = new RedisValue[2 + (counters.Count * 2)];
        args[0] = amountMicroUsd;
        args[1] = missingAsZero ? 1 : 0;
        for (var i = 0; i < counters.Count; i++)
        {
            args[2 + (i * 2)] = counters[i].LimitMicroUsd;
            args[3 + (i * 2)] = (long)Math.Max(60, counters[i].TimeToLive.TotalSeconds);
        }

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(ReserveScript, [.. counters.Select(c => (RedisKey)c.Key)], args))!;
        var status = (int)result[0];
        var items = (RedisResult[])result[1]!;
        return status == -2
            ? new SpendReservation(-1, [], [.. items.Select(r => (int)r)])
            : new SpendReservation(status, [.. items.Select(r => (long)r)], []);
    }

    public async Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, long deltaMicroUsd, CancellationToken cancellationToken)
    {
        // Issued together so they are pipelined: one round trip however many budgets apply.
        var db = redis.GetDatabase();
        return await Task.WhenAll(counters.Select(c => db.StringIncrementAsync(c.Key, deltaMicroUsd)));
    }
}

public sealed partial class RedisInvalidationBus(IConnectionMultiplexer redis, ILogger<RedisInvalidationBus> logger) : IInvalidationBus
{
    private static readonly RedisChannel Channel = RedisKeys.InvalidateChannel;

    public Task PublishAsync(InvalidationKind kind, CancellationToken cancellationToken) =>
        redis.GetSubscriber().PublishAsync(Channel, kind.ToString());

    public IDisposable Subscribe(Action<InvalidationKind> handler)
    {
        var subscriber = redis.GetSubscriber();
        Action<RedisChannel, RedisValue> callback = (_, value) =>
        {
            if (Enum.TryParse<InvalidationKind>(value.ToString(), out var kind))
            {
                handler(kind);
            }
        };
        subscriber.Subscribe(Channel, callback);
        LogSubscribed(logger);
        return new Unsubscriber(() => subscriber.Unsubscribe(Channel, callback));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed to cache invalidation channel")]
    private static partial void LogSubscribed(ILogger logger);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class RedisCircuitBreakerStore(IConnectionMultiplexer redis, IOptions<CircuitBreakerOptions> options) : ICircuitBreakerStore
{
    private const string FailureScript = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        if n >= tonumber(ARGV[2]) then
          redis.call('SET', KEYS[2], 'auto', 'EX', ARGV[3])
          redis.call('DEL', KEYS[1])
          return 1
        end
        return 0
        """;

    // Never shortens a cooldown: SET only creates the key, PEXPIRE GT only lengthens it (Redis 7+; both in the gateway ACL).
    private const string CooldownScript = """
        if not redis.call('SET', KEYS[1], 'retry-after', 'NX', 'PX', ARGV[1]) then
          redis.call('PEXPIRE', KEYS[1], ARGV[1], 'GT')
        end
        return 0
        """;

    private static RedisKey OpenKey(Guid id) => RedisKeys.CircuitOpen(id);
    private static RedisKey FailKey(Guid id) => RedisKeys.CircuitFailures(id);

    public async Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = providerIds.ToArray();
        var values = await redis.GetDatabase().StringGetAsync([.. ids.Select(OpenKey)]);
        return ids.Where((_, i) => values[i].HasValue).ToHashSet();
    }

    public async Task<CircuitStatus> GetStatusAsync(IReadOnlyCollection<Guid> providerIds, IReadOnlyCollection<Guid> deploymentIds, CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0 && deploymentIds.Count == 0)
        {
            return CircuitStatus.None;
        }

        // One MGET for both: circuits by provider, then cooldowns by deployment.
        var providers = providerIds.ToArray();
        var deployments = deploymentIds.ToArray();
        var keys = new RedisKey[providers.Length + deployments.Length];
        for (var i = 0; i < providers.Length; i++)
        {
            keys[i] = OpenKey(providers[i]);
        }

        for (var i = 0; i < deployments.Length; i++)
        {
            keys[providers.Length + i] = RedisKeys.Cooldown(deployments[i]);
        }

        var values = await redis.GetDatabase().StringGetAsync(keys);
        HashSet<Guid>? open = null;
        HashSet<Guid>? cooling = null;
        for (var i = 0; i < values.Length; i++)
        {
            if (!values[i].HasValue)
            {
                continue;
            }

            if (i < providers.Length)
            {
                (open ??= []).Add(providers[i]);
            }
            else
            {
                (cooling ??= []).Add(deployments[i - providers.Length]);
            }
        }

        return open is null && cooling is null ? CircuitStatus.None
            : new CircuitStatus(open ?? CircuitStatus.None.OpenProviders, cooling ?? CircuitStatus.None.CoolingDeployments);
    }

    public Task CoolDownAsync(Guid deploymentId, TimeSpan duration, CancellationToken cancellationToken) =>
        redis.GetDatabase().ScriptEvaluateAsync(CooldownScript, [RedisKeys.Cooldown(deploymentId)], [Math.Max(1, (long)duration.TotalMilliseconds)]);

    public Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var o = options.Value;
        return redis.GetDatabase().ScriptEvaluateAsync(
            FailureScript,
            [FailKey(providerId), OpenKey(providerId)],
            [(long)o.SamplingWindow.TotalSeconds, o.FailureThreshold, (long)o.BreakDuration.TotalSeconds]);
    }

    /// <summary>Fire-and-forget: called on every successful provider call, before the response starts.</summary>
    public Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync(FailKey(providerId), CommandFlags.FireAndForget);

    public async Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        if (state == CircuitState.Open)
        {
            await db.StringSetAsync(OpenKey(providerId), "forced", openFor ?? options.Value.BreakDuration);
        }
        else
        {
            await db.KeyDeleteAsync([OpenKey(providerId), FailKey(providerId)]);
        }
    }
}
