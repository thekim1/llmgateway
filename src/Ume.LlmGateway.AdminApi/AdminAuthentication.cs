using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Ume.LlmGateway.AdminApi;

public static class AdminAuthentication
{
    /// <summary>
    /// Normalises identity-provider claims to the names the API authorises on ("roles", "departmentCodes"):
    /// expands JSON-array claims, copies differently named claims, and derives roles from group membership.
    /// </summary>
    public static void ApplyClaimMapping(ClaimsPrincipal? principal, OidcOptions options)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        var sources = new[] { options.RoleClaim, options.DepartmentClaim, options.GroupClaim };
        foreach (var claim in identity.Claims.Where(c => sources.Contains(c.Type) && c.Value.StartsWith('[')).ToArray())
        {
            using var json = JsonDocument.Parse(claim.Value);
            identity.RemoveClaim(claim);
            foreach (var value in json.RootElement.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    identity.AddClaim(new Claim(claim.Type, value.GetString()!));
                }
            }
        }
        CopyClaims(identity, options.RoleClaim, "roles");
        CopyClaims(identity, options.DepartmentClaim, "departmentCodes");
        var groups = identity.FindAll(options.GroupClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, mapped) in options.RoleGroups)
        {
            if (!identity.HasClaim("roles", role) &&
                mapped.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(groups.Contains))
            {
                identity.AddClaim(new Claim("roles", role));
            }
        }
    }

    private static void CopyClaims(ClaimsIdentity identity, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }
        foreach (var claim in identity.FindAll(from).ToArray())
        {
            identity.AddClaim(new Claim(to, claim.Value));
        }
    }

    public static void MapAdminAuthentication(this WebApplication app)
    {
        app.MapGet("/bff/login", (string? returnUrl) =>
        {
            var destination = returnUrl ?? "/";
            if (!destination.StartsWith('/') || destination.StartsWith("//", StringComparison.Ordinal) || destination.Contains('\\', StringComparison.Ordinal) ||
                destination.Any(char.IsControl))
            {
                return Results.Problem(statusCode: 400, detail: "Returadressen måste vara en lokal sökväg.");
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = destination }, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
        app.MapGet("/bff/user", async (HttpContext http, IAntiforgery antiforgery, IHostEnvironment environment) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                Secure = true, HttpOnly = false, SameSite = SameSiteMode.Lax, Path = "/",
            });
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new
            {
                isAuthenticated = http.User.Identity?.IsAuthenticated == true,
                environment = environment.EnvironmentName,
                name = http.User.Identity?.Name,
                email = http.User.FindFirstValue("email"),
                roles = http.User.FindAll("roles").Select(c => c.Value).ToArray(),
                departmentCodes = http.User.FindAll("departmentCodes").Select(c => c.Value).ToArray(),
                sessionExpiresAt = auth.Properties?.ExpiresUtc,
            });
        });
        app.MapPost("/bff/logout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])).RequireAuthorization();
        app.MapPost("/bff/session/extend", async (HttpContext http, TimeProvider time) =>
        {
            var expires = time.GetUtcNow().AddHours(8);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, http.User,
                new AuthenticationProperties { ExpiresUtc = expires, IsPersistent = false });
            return Results.Ok(new { sessionExpiresAt = expires });
        }).RequireAuthorization();
    }
}

public sealed class AdminAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if ((http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || http.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase)) &&
            !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
        {
            if (http.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
            {
                await Results.Problem(statusCode: 400, detail: "CSRF-skydd saknas. Läs in sidan igen.").ExecuteAsync(http);
                return;
            }
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, detail: "CSRF-token är ogiltig. Läs in sidan igen.").ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }
}
