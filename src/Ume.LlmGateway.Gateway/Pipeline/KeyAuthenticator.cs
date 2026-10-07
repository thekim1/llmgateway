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
/// </summary>
public sealed class KeyAuthenticator : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly VirtualKeyHasher _hasher;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly IDisposable _subscription;
    private CancellationTokenSource _generation = new();

    public KeyAuthenticator(VirtualKeyHasher hasher, IServiceScopeFactory scopes, IOptionsMonitor<GatewayOptions> options, IInvalidationBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _hasher = hasher;
        _scopes = scopes;
        _options = options;
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
        if (_cache.TryGetValue(hash, out VirtualKey? cached))
        {
            return cached;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var key = await db.VirtualKeys.AsNoTracking()
            .Include(k => k.Team).ThenInclude(t => t!.Department)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        if (key is null)
        {
            return null;
        }

        var seconds = _options.CurrentValue.KeyCacheSeconds;
        if (seconds > 0)
        {
            using var entry = _cache.CreateEntry(hash);
            entry.Value = key;
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
            entry.Size = 1;
            entry.AddExpirationToken(new CancellationChangeToken(_generation.Token));
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
    }
}
