using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Integrations;

/// <summary>
/// Credential keys of active integrations, looked up by the key's HMAC (the gateway's pepper and hasher) and cached for
/// <see cref="CacheFor"/>; every key, team, department or integration change publishes <see cref="InvalidationKind.Keys"/>,
/// which empties the cache. Unknown keys are cached as misses too, so a client retrying with a wrong key costs no query.
/// </summary>
public sealed class IntegrationKeyStore : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly IServiceScopeFactory _scopes;
    private readonly IDisposable _subscription;

    public IntegrationKeyStore(IServiceScopeFactory scopes, IInvalidationBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _scopes = scopes;
        _subscription = bus.Subscribe(kind =>
        {
            if (kind == InvalidationKind.Keys)
            {
                _cache.Clear();
            }
        });
    }

    /// <summary>The key with <paramref name="hash"/>, its team, department and integration; null when there is none.</summary>
    public async Task<VirtualKey?> FindAsync(string hash, CancellationToken ct)
    {
        if (_cache.TryGetValue(hash, out VirtualKey? cached))
        {
            return cached;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var key = await db.VirtualKeys.AsNoTracking().Include(k => k.Team!).ThenInclude(t => t.Department).Include(k => k.Integration)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        key?.IndexAllowLists();
        using var entry = _cache.CreateEntry(hash);
        entry.Value = key;
        entry.AbsoluteExpirationRelativeToNow = CacheFor;
        entry.Size = 1;
        return key;
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _cache.Dispose();
    }
}

/// <summary>
/// The Integration API's authentication (docs/integrations.md): <c>Authorization: Bearer ume-sk-…</c> with a credential
/// key of an active integration. The key's status, expiry, grace period, owner and client address list apply as on
/// the gateway; an assigned or ordinary key is refused. The principal carries the key's scopes, which the endpoints'
/// policies require, and its name is <c>integration:&lt;integration&gt;</c> for logs and security events.
/// </summary>
public sealed class IntegrationKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    VirtualKeyHasher hasher, IntegrationKeyStore keys, AdminClientAddresses addresses, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationKey";
    public const string ScopeClaim = "ume:integration_scope";
    public const string IntegrationClaim = "ume:integration";
    public const string KeyClaim = "ume:key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = header["Bearer ".Length..].Trim();
        if (!VirtualKeyHasher.LooksLikeKey(presented) || await keys.FindAsync(hasher.Hash(presented), Context.RequestAborted) is not { } key)
        {
            return AuthenticateResult.Fail("The key is missing or invalid.");
        }

        if (!key.IsUsable(time.GetUtcNow()) || key.Team is not { IsActive: true, Department.IsActive: true })
        {
            return AuthenticateResult.Fail("The key is revoked, expired or disabled, or its team or department is deactivated.");
        }

        if (key is not { IntegrationRole: IntegrationRole.Credential, Integration.IsActive: true })
        {
            return AuthenticateResult.Fail("Only a credential key of an active integration can use the Integration API.");
        }

        if (!key.IsAddressAllowed(await addresses.ClientAddressAsync(Context)))
        {
            return AuthenticateResult.Fail("The key may not be used from this address.");
        }

        Claim[] claims =
        [
            new(AdminClaims.Subject, $"integration-key:{key.Id}"),
            new(ClaimTypes.Name, $"integration:{key.Integration!.Name}"),
            new(IntegrationClaim, key.Integration!.Id.ToString()),
            new(KeyClaim, key.Id.ToString()),
            .. key.IntegrationScopes.Select(s => new Claim(ScopeClaim, s)),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, AdminClaims.Roles));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>The integration and credential key of the signed-in principal (from the claims above).</summary>
    public static (Guid IntegrationId, Guid KeyId) Caller(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (Guid.Parse(user.FindFirst(IntegrationClaim)!.Value), Guid.Parse(user.FindFirst(KeyClaim)!.Value));
    }

    /// <summary>The policy name for an endpoint that needs <paramref name="scope"/>.</summary>
    public static string Policy(string scope) => "integration:" + scope;

    /// <summary>One policy per known scope: this scheme only, the scope claim required.</summary>
    public static void AddPolicies(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var scope in IntegrationScopes.All)
        {
            options.AddPolicy(Policy(scope), p => p.AddAuthenticationSchemes(SchemeName).RequireAuthenticatedUser().RequireClaim(ScopeClaim, scope));
        }
    }
}
