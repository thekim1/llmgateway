using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>
/// Redis-backed stores. Only counters, circuit flags and invalidation signals are stored – never prompt or
/// response content, never key material. All keys are namespaced with <c>ume:</c>.
/// </summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, TimeProvider time) : IRateLimiter
{
    private const string Script = """
        local tpm = tonumber(ARGV[2])
        local tok = tonumber(redis.call('GET', KEYS[2]) or '0')
        if tpm > 0 and tok >= tpm then return {-2, tok} end
        local req = redis.call('INCR', KEYS[1])
        if req == 1 then redis.call('EXPIRE', KEYS[1], ARGV[3]) end
        local rpm = tonumber(ARGV[1])
        if rpm > 0 and req > rpm then return {-1, req} end
        return {req, tok}
        """;

    public async Task<RateLimitDecision> AcquireAsync(Guid keyId, int? requestsPerMinute, int? tokensPerMinute, CancellationToken cancellationToken)
    {
        if (requestsPerMinute is not > 0 && tokensPerMinute is not > 0)
        {
            return RateLimitDecision.Unlimited;
        }

        var (minute, retry) = InMemoryRateLimiter.Window(time.GetUtcNow());
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [$"ume:rl:req:{keyId:N}:{minute}", $"ume:rl:tok:{keyId:N}:{minute}"],
            [requestsPerMinute ?? 0, tokensPerMinute ?? 0, 120]))!;

        var status = (long)result[0];
        return status switch
        {
            -2 => new RateLimitDecision(false, requestsPerMinute, 0, retry, "tokens"),
            -1 => new RateLimitDecision(false, requestsPerMinute, 0, retry, "requests"),
            _ => new RateLimitDecision(true, requestsPerMinute,
                requestsPerMinute is > 0 ? (int)Math.Max(0, requestsPerMinute.Value - status) : null, TimeSpan.Zero, null),
        };
    }

    public async Task RecordTokensAsync(Guid keyId, long tokens, CancellationToken cancellationToken)
    {
        if (tokens <= 0)
        {
            return;
        }

        var (minute, _) = InMemoryRateLimiter.Window(time.GetUtcNow());
        var key = $"ume:rl:tok:{keyId:N}:{minute}";
        var db = redis.GetDatabase();
        var increment = db.StringIncrementAsync(key, tokens);
        _ = db.KeyExpireAsync(key, TimeSpan.FromSeconds(120), CommandFlags.FireAndForget); // pipelined after the INCRBY
        await increment;
    }

    public async Task<double?> PeekTokensUsedPercentAsync(Guid keyId, int? tokensPerMinute, CancellationToken cancellationToken)
    {
        if (tokensPerMinute is not > 0)
        {
            return null;
        }

        var (minute, _) = InMemoryRateLimiter.Window(time.GetUtcNow());
        var used = await redis.GetDatabase().StringGetAsync($"ume:rl:tok:{keyId:N}:{minute}");
        return InMemoryRateLimiter.TokenPercent(used.HasValue ? (long)used : 0, tokensPerMinute.Value);
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

    private static RedisKey Key(Guid keyId) => $"ume:rt:{keyId:N}";
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

    public Task InitializeAsync(string key, long valueMicroSek, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(key, valueMicroSek, timeToLive, When.NotExists);

    public async Task<int> TryReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroSek, CancellationToken cancellationToken) =>
        (await ReserveAsync(counters, amountMicroSek, missingAsZero: true, cancellationToken)).ExhaustedIndex;

    public async Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroSek, bool missingAsZero, CancellationToken cancellationToken)
    {
        if (counters.Count == 0)
        {
            return new SpendReservation(-1, [], []);
        }

        var args = new RedisValue[2 + (counters.Count * 2)];
        args[0] = amountMicroSek;
        args[1] = missingAsZero ? 1 : 0;
        for (var i = 0; i < counters.Count; i++)
        {
            args[2 + (i * 2)] = counters[i].LimitMicroSek;
            args[3 + (i * 2)] = (long)Math.Max(60, counters[i].TimeToLive.TotalSeconds);
        }

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(ReserveScript, [.. counters.Select(c => (RedisKey)c.Key)], args))!;
        var status = (int)result[0];
        var items = (RedisResult[])result[1]!;
        return status == -2
            ? new SpendReservation(-1, [], [.. items.Select(r => (int)r)])
            : new SpendReservation(status, [.. items.Select(r => (long)r)], []);
    }

    public async Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, long deltaMicroSek, CancellationToken cancellationToken)
    {
        // Issued together so they are pipelined: one round trip however many budgets apply.
        var db = redis.GetDatabase();
        return await Task.WhenAll(counters.Select(c => db.StringIncrementAsync(c.Key, deltaMicroSek)));
    }
}

public sealed partial class RedisInvalidationBus(IConnectionMultiplexer redis, ILogger<RedisInvalidationBus> logger) : IInvalidationBus
{
    private static readonly RedisChannel Channel = RedisChannel.Literal("ume:invalidate");

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

    private static string OpenKey(Guid id) => $"ume:circuit:{id:N}";
    private static string FailKey(Guid id) => $"ume:circuit-fail:{id:N}";

    public async Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = providerIds.ToArray();
        var values = await redis.GetDatabase().StringGetAsync([.. ids.Select(id => (RedisKey)OpenKey(id))]);
        return ids.Where((_, i) => values[i].HasValue).ToHashSet();
    }

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
