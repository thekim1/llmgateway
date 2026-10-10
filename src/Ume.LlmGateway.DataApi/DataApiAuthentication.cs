using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.DataApi;

/// <summary>
/// OAuth 2.0 client credentials: every integration (ETL job, SIEM connector) is its own client at the organisation's
/// identity provider and gets only the permissions it needs. Works with Keycloak, AD FS and Entra ID.
/// </summary>
public static class DataApiAuthentication
{
    public static IHostApplicationBuilder AddDataApiAuthentication(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Configuration.GetSection(DataApiOptions.SectionName).Get<DataApiOptions>() ?? new DataApiOptions();
        if (string.IsNullOrWhiteSpace(options.Authority) && !builder.Environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException("DataApi:Authority must be set to the OIDC issuer that signs the clients' access tokens.");
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.Authority = options.Authority;
            o.Audience = options.Audience;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            o.TokenValidationParameters.ValidateAudience = true;
            o.TokenValidationParameters.ValidateIssuer = true;
            o.TokenValidationParameters.ValidateLifetime = true;
        });

        builder.Services.AddAuthorization(o =>
        {
            foreach (var permission in DataPermissions.All)
            {
                o.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
                    HasPermission(ctx.User, permission, ctx.Resource is HttpContext http
                        ? http.RequestServices.GetRequiredService<IOptions<DataApiOptions>>().Value.PermissionClaims
                        : options.PermissionClaims)));
            }
        });
        return builder;
    }

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
        if (http.User.Identity?.IsAuthenticated == true && http.Request.Path.StartsWithSegments("/v1"))
        {
            var logger = SecurityEvents.CreateLogger(http.RequestServices.GetRequiredService<ILoggerFactory>());
            SecurityEvents.DataRead(logger, ClientId(http.User), http.Request.Path.Value ?? string.Empty, http.Response.StatusCode,
                http.Response.Headers["X-Next-Cursor"].ToString() is { Length: > 0 } cursor ? cursor : null);
        }
    }
}
