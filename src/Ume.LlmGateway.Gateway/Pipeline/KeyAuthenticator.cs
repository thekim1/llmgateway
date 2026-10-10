using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Resolves a presented virtual key. Only the HMAC hash is used for lookup; the plaintext key is never logged,
/// cached or stored. Entries are cached briefly and dropped instantly when the admin API publishes a revocation.
/// An entry older than the TTL is still served while one background lookup refreshes it, and concurrent misses for
/// the same key share a single database query. Unknown keys are remembered for the same TTL in a separate, smaller
/// cache, so a client retrying with a deleted or mistyped key does not cost a query per request, and a flood of made-up
/// keys cannot evict real ones. Creating a key publishes an invalidation, which clears both caches.
/// </summary>
public sealed class KeyAuthenticator : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly MemoryCache _unknown = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly ConcurrentDictionary<string, Task<VirtualKey?>> _loading = new(StringComparer.Ordinal);
    private readonly VirtualKeyHasher _hasher;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private CancellationTokenSource _generation = new();

    private sealed record CachedKey(VirtualKey Key, DateTimeOffset LoadedAt);

    public KeyAuthenticator(VirtualKeyHasher hasher, IServiceScopeFactory scopes, IOptionsMonitor<GatewayOptions> options, IInvalidationBus bus, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _hasher = hasher;
        _scopes = scopes;
        _options = options;
        _time = time;
        _subscription = bus.Subscribe(kind =>
        {
            if (kind == InvalidationKind.Keys)
            {
                InvalidateAll();
            }
        });
    }

    public static string? ExtractKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }

        // Anthropic SDKs use x-api-key; Azure OpenAI SDKs use api-key.
        foreach (var header in (string[])["x-api-key", "api-key"])
        {
            if (request.Headers.TryGetValue(header, out var value) && !StringValues.IsNullOrEmpty(value))
            {
                return value.ToString().Trim();
            }
        }

        return null;
    }

    public async Task<VirtualKey?> FindAsync(string? presentedKey, CancellationToken cancellationToken)
    {
        if (!VirtualKeyHasher.LooksLikeKey(presentedKey))
        {
            return null;
        }

        var hash = _hasher.Hash(presentedKey!);
        var ttl = TimeSpan.FromSeconds(_options.CurrentValue.KeyCacheSeconds);
        if (_cache.TryGetValue(hash, out CachedKey? cached) && cached is not null)
        {
            if (_time.GetUtcNow() - cached.LoadedAt >= ttl)
            {
                // Refresh in the background; a failure keeps the cached entry and surfaces on the next miss.
                _ = LoadSharedAsync(hash, ttl).ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            return cached.Key;
        }

        if (_unknown.TryGetValue(hash, out DateTimeOffset unknownSince) && _time.GetUtcNow() - unknownSince < ttl)
        {
            return null;
        }

        return await LoadSharedAsync(hash, ttl).WaitAsync(cancellationToken);
    }

    /// <summary>One database lookup per key hash at a time, not tied to any single request's cancellation.</summary>
    private Task<VirtualKey?> LoadSharedAsync(string hash, TimeSpan ttl)
    {
        if (_loading.TryGetValue(hash, out var running))
        {
            return running;
        }

        var load = new TaskCompletionSource<VirtualKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_loading.TryAdd(hash, load.Task))
        {
            return _loading.TryGetValue(hash, out running) ? running : LoadSharedAsync(hash, ttl);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                load.SetResult(await LoadAsync(hash, ttl));
            }
            catch (Exception ex)
            {
                load.SetException(ex);
            }
            finally
            {
                _loading.TryRemove(hash, out _);
            }
        });
        return load.Task;
    }

    private async Task<VirtualKey?> LoadAsync(string hash, TimeSpan ttl)
    {
        // Captured before the query: if a revocation lands while it runs, the result must not be cached.
        CancellationToken generation;
        try
        {
            generation = Volatile.Read(ref _generation).Token;
        }
        catch (ObjectDisposedException)
        {
            generation = new CancellationToken(canceled: true); // invalidated this instant: don't cache
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var key = await db.VirtualKeys.AsNoTracking()
            .Include(k => k.Team).ThenInclude(t => t!.Department)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, CancellationToken.None);
        if (key is null)
        {
            _cache.Remove(hash);
            if (ttl > TimeSpan.Zero && !generation.IsCancellationRequested)
            {
                using var unknown = _unknown.CreateEntry(hash);
                // Freshness is checked against TimeProvider like the positive cache; the cache's own expiry only evicts.
                unknown.Value = _time.GetUtcNow();
                unknown.AbsoluteExpirationRelativeToNow = ttl;
                unknown.Size = 1;
                unknown.AddExpirationToken(new CancellationChangeToken(generation));
            }

            return null;
        }

        // Read-only from here on: the allow-list is indexed once instead of scanned on every request.
        key.IndexAllowedModels();
        if (ttl > TimeSpan.Zero && !generation.IsCancellationRequested)
        {
            using var entry = _cache.CreateEntry(hash);
            entry.Value = new CachedKey(key, _time.GetUtcNow());
            // Kept past the TTL so a busy key is refreshed in the background instead of missing; idle keys drop out.
            entry.AbsoluteExpirationRelativeToNow = ttl * 2;
            entry.Size = 1;
            entry.AddExpirationToken(new CancellationChangeToken(generation));
        }

        return key;
    }

    public void InvalidateAll()
    {
        var old = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _generation.Dispose();
        _cache.Dispose();
        _unknown.Dispose();
    }
}
