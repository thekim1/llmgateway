using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

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
