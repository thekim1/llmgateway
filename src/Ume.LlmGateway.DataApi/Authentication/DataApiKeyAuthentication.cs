using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.DataApi.Authentication;

/// <summary>A Data API key as the API needs it to authenticate; never the key or its hash.</summary>
public sealed record DataApiKeyEntry(Guid Id, string Name, string Prefix, IReadOnlyList<string> Permissions, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)
{
    public KeyStatus Status(DateTimeOffset now) =>
        RevokedAt is { } revoked && revoked <= now ? KeyStatus.Revoked
        : ExpiresAt is { } expires && expires <= now ? KeyStatus.Expired
        : KeyStatus.Active;
}

/// <summary>
/// Looks Data API keys up by hash with a short in-memory cache (<see cref="DataApiOptions.KeyCacheSeconds"/>), so a
/// revoked key keeps working on an instance for at most that long. Unknown hashes are cached too, so repeating a
/// wrong key costs no query; the cache is size-limited, so made-up keys cannot grow it without bound.
/// </summary>
public sealed partial class DataApiKeyStore(IServiceScopeFactory scopes, IOptionsMonitor<DataApiOptions> options, TimeProvider time, ILoggerFactory loggers) : IDisposable
{
    /// <summary>How often a key's <c>LastUsedAt</c> is written at most (per instance, and skipped when another instance wrote it).</summary>
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RefusedLogInterval = TimeSpan.FromMinutes(1);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTouched = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastRefusedLog = new();
    private readonly ILogger _logger = loggers.CreateLogger<DataApiKeyStore>();
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public async Task<DataApiKeyEntry?> FindAsync(string hash, CancellationToken ct)
    {
        if (_cache.TryGetValue(hash, out DataApiKeyEntry? cached))
        {
            return cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var row = await db.DataApiKeys.AsNoTracking().Where(k => k.KeyHash == hash)
            .Select(k => new { k.Id, k.Name, k.Prefix, k.Permissions, k.ExpiresAt, k.RevokedAt }).SingleOrDefaultAsync(ct);
        var entry = row is null ? null : new DataApiKeyEntry(row.Id, row.Name, row.Prefix, [.. row.Permissions], row.ExpiresAt, row.RevokedAt);
        var seconds = options.CurrentValue.KeyCacheSeconds;
        if (seconds > 0)
        {
            _cache.Set(hash, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds) });
        }

        return entry;
    }

    /// <summary>Records that the key was used, at most every <see cref="LastUsedInterval"/>. A failure is logged, never thrown.</summary>
    public async Task TouchAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_lastTouched.TryGetValue(id, out var last) && now - last < LastUsedInterval)
        {
            return;
        }

        _lastTouched[id] = now;
        var threshold = now - LastUsedInterval;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.DataApiKeys.Where(k => k.Id == id && (k.LastUsedAt == null || k.LastUsedAt < threshold))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }
        catch (Exception e) when (e is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            // Older installs may not have granted UPDATE ("LastUsedAt") to the data role yet; reads must still work.
            LastUsedFailed(_logger, e, id);
        }
    }

    /// <summary>Logs a <c>data.auth.failed</c> security event for a known key, at most once a minute per key.</summary>
    public void ReportRefused(DataApiKeyEntry key, KeyStatus status)
    {
        var now = time.GetUtcNow();
        if (_lastRefusedLog.TryGetValue(key.Id, out var last) && now - last < RefusedLogInterval)
        {
            return;
        }

        _lastRefusedLog[key.Id] = now;
        SecurityEvents.DataKeyRefused(_security, status == KeyStatus.Revoked ? "key_revoked" : "key_expired", key.Id, key.Prefix);
    }

    public void Dispose() => _cache.Dispose();

    [LoggerMessage(LogLevel.Warning, "Could not record the last use of Data API key {KeyId}")]
    private static partial void LastUsedFailed(ILogger logger, Exception exception, Guid keyId);
}

/// <summary>
/// Authenticates <c>Authorization: Bearer ume-dk-…</c> or <c>X-Api-Key: ume-dk-…</c>. The principal carries the key's
/// permissions in <see cref="PermissionClaim"/> and <c>client_id</c> <c>key:&lt;name&gt;</c>, which identifies the key in
/// <c>data.read</c> events and partitions the rate limit.
/// </summary>
public sealed class DataApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, DataApiKeyStore store, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DataApiKey";
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Claim that holds a key's permissions; only read for principals of this scheme.</summary>
    public const string PermissionClaim = "ume_data_permission";

    private const string BearerPrefix = "Bearer ";

    /// <summary>True when the request presents something meant as a Data API key (rather than an access token).</summary>
    public static bool PresentsKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.ContainsKey(ApiKeyHeader)
            || (request.Headers[HeaderNames.Authorization].ToString() is var auth && auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                && auth.AsSpan(BearerPrefix.Length).Trim().StartsWith(DataApiKeyHasher.KeyPrefix, StringComparison.Ordinal));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string presented;
        if (Request.Headers.TryGetValue(ApiKeyHeader, out var header))
        {
            presented = header.ToString().Trim();
        }
        else if (Request.Headers[HeaderNames.Authorization].ToString() is var auth && auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            presented = auth[BearerPrefix.Length..].Trim();
        }
        else
        {
            return AuthenticateResult.NoResult();
        }

        // Nothing derived from an unrecognised value is logged: people sometimes send the wrong secret.
        if (!DataApiKeyHasher.LooksLikeKey(presented) || await store.FindAsync(DataApiKeyHasher.Hash(presented), Context.RequestAborted) is not { } key)
        {
            return AuthenticateResult.Fail("invalid_key");
        }

        var status = key.Status(time.GetUtcNow());
        if (status != KeyStatus.Active)
        {
            store.ReportRefused(key, status);
            return AuthenticateResult.Fail(status == KeyStatus.Revoked ? "key_revoked" : "key_expired");
        }

        await store.TouchAsync(key.Id, Context.RequestAborted);
        List<Claim> claims = [new("client_id", "key:" + key.Name), new("sub", "data-api-key:" + key.Id), new("key_prefix", key.Prefix)];
        claims.AddRange(key.Permissions.Select(p => new Claim(PermissionClaim, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer");
        return Task.CompletedTask;
    }
}
