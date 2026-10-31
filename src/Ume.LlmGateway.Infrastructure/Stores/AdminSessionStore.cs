using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

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

public sealed class RedisAdminSessionStore(IConnectionMultiplexer redis) : IAdminSessionStore
{
    public Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(RedisKeys.AdminSession(sessionId), value, timeToLive);

    public async Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        (byte[]?)await redis.GetDatabase().StringGetAsync(RedisKeys.AdminSession(sessionId));

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync(RedisKeys.AdminSession(sessionId));
}
