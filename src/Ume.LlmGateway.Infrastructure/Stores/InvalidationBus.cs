using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

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
