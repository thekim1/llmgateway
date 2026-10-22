using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.DataApi;

/// <summary>
/// Two ways in, both per integration: OAuth 2.0 client credentials (every ETL job or SIEM connector is its own client at
/// the organisation's identity provider; Keycloak, AD FS, Entra ID) and Data API keys created by gateway admins in the
/// admin UI. A request presenting a key (<c>Bearer ume-dk-…</c> or <c>X-Api-Key</c>) goes to the key handler, anything
/// else to JWT validation.
/// </summary>
public static class DataApiAuthentication
{
    /// <summary>The default scheme: forwards to <see cref="DataApiKeyHandler.SchemeName"/> or JWT bearer per request.</summary>
    public const string SchemeName = "DataApi";

    public static IHostApplicationBuilder AddDataApiAuthentication(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Configuration.GetSection(DataApiOptions.SectionName).Get<DataApiOptions>() ?? new DataApiOptions();
        var testing = builder.Environment.IsEnvironment("Testing");
        var useTokens = !string.IsNullOrWhiteSpace(options.Authority) || testing;
        if (!useTokens && !options.AllowKeys)
        {
            throw new InvalidOperationException(
                "The Data API accepts no credentials: set DataApi:Authority to the OIDC issuer that signs the clients' access tokens, or leave DataApi:AllowKeys on.");
        }

        var authentication = builder.Services.AddAuthentication(SchemeName)
            .AddPolicyScheme(SchemeName, "Data API key or access token", o => o.ForwardDefaultSelector = http =>
                !useTokens || (options.AllowKeys && DataApiKeyHandler.PresentsKey(http.Request)) ? DataApiKeyHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme);
        if (useTokens)
        {
            authentication.AddJwtBearer(o =>
            {
                o.Authority = options.Authority;
                o.Audience = options.Audience;
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                o.TokenValidationParameters.ValidateAudience = true;
                o.TokenValidationParameters.ValidateIssuer = true;
                o.TokenValidationParameters.ValidateLifetime = true;
            });
        }

        // With keys off the selector never picks this scheme, so a presented key goes to JWT validation and is refused there.
        authentication.AddScheme<AuthenticationSchemeOptions, DataApiKeyHandler>(DataApiKeyHandler.SchemeName, null);
        builder.Services.AddSingleton<DataApiKeyStore>();
        builder.Services.AddAuthorization(o =>
        {
            foreach (var permission in DataPermissions.All)
            {
                o.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
                    HasPermission(ctx.User, permission, PermissionClaims(ctx.User, ctx.Resource is HttpContext http
                        ? http.RequestServices.GetRequiredService<IOptions<DataApiOptions>>().Value.PermissionClaims
                        : options.PermissionClaims))));
            }
        });
        return builder;
    }

    /// <summary>A key's permissions are in its own claim; a token's in the configured claims. Neither can borrow the other's.</summary>
    private static IEnumerable<string> PermissionClaims(ClaimsPrincipal user, IEnumerable<string> tokenClaims) =>
        user.Identity?.AuthenticationType == DataApiKeyHandler.SchemeName ? [DataApiKeyHandler.PermissionClaim] : tokenClaims;

    /// <summary>True when any configured claim grants the permission. Scope claims are space-separated lists.</summary>
    public static bool HasPermission(ClaimsPrincipal user, string permission, IEnumerable<string> claimTypes)
    {
        ArgumentNullException.ThrowIfNull(user);
        return claimTypes.SelectMany(user.FindAll)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(permission, StringComparer.Ordinal);
    }

    /// <summary>The calling client: Keycloak and Entra ID v2 use <c>azp</c>, Entra ID v1 <c>appid</c>, RFC 9068 <c>client_id</c>.</summary>
    public static string ClientId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindFirstValue("client_id") ?? user.FindFirstValue("azp") ?? user.FindFirstValue("appid")
            ?? user.FindFirstValue("sub") ?? "unknown";
    }

    /// <summary>Logs every data read as a <c>data.read</c> security event: who read which feed, and how many rows.</summary>
    public static async Task LogReadsAsync(HttpContext http, Func<Task> next)
    {
        await next();
        if (http.User.Identity?.IsAuthenticated == true && http.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase) &&
            SecurityEvents.CreateLogger(http.RequestServices.GetRequiredService<ILoggerFactory>()) is var logger && logger.IsEnabled(LogLevel.Information))
        {
            SecurityEvents.DataRead(logger, ClientId(http.User), SecurityEvents.Printable(http.Request.Path.Value ?? string.Empty), http.Response.StatusCode,
                http.Response.Headers["X-Next-Cursor"].ToString() is { Length: > 0 } cursor ? cursor : null);
        }
    }
}
