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
        await db.StringIncrementAsync(key, tokens);
        await db.KeyExpireAsync(key, TimeSpan.FromSeconds(120));
    }
}

public sealed class RedisSpendLedger(IConnectionMultiplexer redis) : ISpendLedger
{
    private const string ReserveScript = """
        local amount = tonumber(ARGV[1])
        for i = 1, #KEYS do
          local v = tonumber(redis.call('GET', KEYS[i]) or '0')
          local limit = tonumber(ARGV[i * 2])
          if v >= limit or amount > limit - v then return i - 1 end
        end
        for i = 1, #KEYS do
          redis.call('INCRBY', KEYS[i], amount)
          redis.call('EXPIRE', KEYS[i], ARGV[i * 2 + 1])
        end
        return -1
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

    public async Task<int> TryReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroSek, CancellationToken cancellationToken)
    {
        if (counters.Count == 0)
        {
            return -1;
        }

        var args = new List<RedisValue> { amountMicroSek };
        foreach (var c in counters)
        {
            args.Add(c.LimitMicroSek);
            args.Add((long)Math.Max(60, c.TimeToLive.TotalSeconds));
        }

        var result = await redis.GetDatabase().ScriptEvaluateAsync(ReserveScript, [.. counters.Select(c => (RedisKey)c.Key)], [.. args]);
        return (int)result;
    }

    public async Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, long deltaMicroSek, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var results = new List<long>(counters.Count);
        foreach (var c in counters)
        {
            results.Add(await db.StringIncrementAsync(c.Key, deltaMicroSek));
        }

        return results;
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

    public Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync(FailKey(providerId));

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
