using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Security;

public static class AdminAuthentication
{
    /// <summary>
    /// Normalises identity-provider claims to the names the API authorises on ("roles", "departmentCodes", "teamCodes"):
    /// expands JSON-array claims, copies differently named claims, and derives roles from group membership.
    /// </summary>
    public static void ApplyClaimMapping(ClaimsPrincipal? principal, OidcOptions options)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        var sources = new[] { options.RoleClaim, options.DepartmentClaim, options.TeamClaim, options.GroupClaim };
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
        CopyClaims(identity, options.RoleClaim, AdminClaims.Roles);
        CopyClaims(identity, options.DepartmentClaim, AdminClaims.DepartmentCodes);
        CopyClaims(identity, options.TeamClaim, AdminClaims.TeamCodes);
        var groups = identity.FindAll(options.GroupClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, mapped) in options.RoleGroups)
        {
            if (!identity.HasClaim(AdminClaims.Roles, role) &&
                mapped.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(groups.Contains))
            {
                identity.AddClaim(new Claim(AdminClaims.Roles, role));
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
            // The group claim may serve as the team claim too; a group is then copied once however many settings name it.
            if (!identity.HasClaim(to, claim.Value))
            {
                identity.AddClaim(new Claim(to, claim.Value));
            }
        }
    }

    /// <summary>
    /// Cookie session (BFF) with OpenID Connect sign-in, antiforgery for the SPA and the authorization policies. The OIDC
    /// authority and client are read from <see cref="OidcOptions"/> when the handler is first used.
    /// </summary>
    public static void AddAdminAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-XSRF-TOKEN";
            o.Cookie.Name = "__Host-ume-csrf";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
        });
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddCookie(o =>
        {
            o.Cookie.Name = "__Host-ume-admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = AdminSessionLimit.Lifetime;
            o.SlidingExpiration = false;
            o.Events.OnSigningIn = ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                AdminSessionLimit.OnSigningIn(ctx, TimeSpan.FromHours(services.GetRequiredService<IOptions<AdminOptions>>().Value.MaxSessionHours),
                    services.GetRequiredService<TimeProvider>().GetUtcNow());
                // A new sign-in (not an extension, which signs in again with the same principal) gets its session id.
                if (ctx.Principal?.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == AdminClaims.Session))
                {
                    identity.AddClaim(new Claim(AdminClaims.Session, AdminActor.NewSessionId()));
                    var actor = AdminActor.Of(ctx.HttpContext, ctx.Principal);
                    SecurityEvents.AdminSignIn(SecurityEvents.CreateLogger(services.GetRequiredService<ILoggerFactory>()), actor.Subject,
                        actor.SourceAddress, actor.Roles, actor.Departments, actor.SessionId);
                }

                return Task.CompletedTask;
            };
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        }).AddOpenIdConnect(o =>
        {
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = AdminClaims.Roles;
            o.Scope.Add("email");
            o.Events.OnTokenValidated = ctx =>
            {
                AdminAuthentication.ApplyClaimMapping(ctx.Principal, ctx.HttpContext.RequestServices.GetRequiredService<IOptions<OidcOptions>>().Value);
                return Task.CompletedTask;
            };
            // An expired session on an API call must answer 401 so the UI can send the user to sign in; a redirect to the
            // identity provider would be followed by fetch and fail on CORS. Only /bff/login starts a real sign-in.
            o.Events.OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.HandleResponse();
                }
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToIdentityProviderForSignOut = async ctx =>
            {
                ctx.ProtocolMessage.ClientId = ctx.Options.ClientId;
                ctx.ProtocolMessage.State = ctx.Options.StateDataFormat.Protect(ctx.Properties);
                if (string.IsNullOrEmpty(ctx.ProtocolMessage.IssuerAddress))
                {
                    throw new InvalidOperationException("OIDC end-session endpoint is missing.");
                }
                await ctx.Response.WriteAsJsonAsync(new { redirectUrl = ctx.ProtocolMessage.CreateLogoutRequestUrl() });
                ctx.HandleResponse();
            };
        });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(AdminPolicies.Admin, p => p.RequireRole(AdminRoles.GatewayAdmin));
            o.AddPolicy(AdminPolicies.Manage, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin));
            o.AddPolicy(AdminPolicies.Read, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.Viewer));
            o.AddPolicy(AdminPolicies.ManageKeys, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.TeamAdmin));
            o.AddPolicy(AdminPolicies.ListTeams, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.Viewer, AdminRoles.TeamAdmin));
        });
        // Sessions live on the server: the cookie only names one, and signing out deletes it (a copied cookie stops working).
        builder.Services.AddSingleton<ITicketStore, AdminTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);
        // Read from the validated options when the handler is first used, not while services are registered.
        builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure<IOptions<OidcOptions>>((o, oidc) =>
        {
            o.Authority = oidc.Value.Authority;
            o.ClientId = oidc.Value.ClientId;
            o.ClientSecret = oidc.Value.ClientSecret;
            foreach (var scope in oidc.Value.ExtraScopes.Where(scope => !string.IsNullOrWhiteSpace(scope)))
            {
                o.Scope.Add(scope);
            }
        });
    }

    public static void MapAdminAuthentication(this WebApplication app)
    {
        app.MapGet("/bff/login", (string? returnUrl) =>
        {
            var destination = returnUrl ?? "/";
            if (!destination.StartsWith('/') || destination.StartsWith("//", StringComparison.Ordinal) || destination.Contains('\\', StringComparison.Ordinal) ||
                destination.Any(char.IsControl))
            {
                return Results.Problem(statusCode: 400, detail: "The return address must be a local path.");
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = destination }, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
        app.MapGet("/bff/user", async (HttpContext http, IAntiforgery antiforgery, IHostEnvironment environment, IOptions<AdminOptions> admin) =>
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
                roles = http.User.FindAll(AdminClaims.Roles).Select(c => c.Value).ToArray(),
                departmentCodes = http.User.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).ToArray(),
                teamCodes = http.User.FindAll(AdminClaims.TeamCodes).Select(c => c.Value).ToArray(),
                sessionExpiresAt = auth.Properties?.ExpiresUtc,
                sessionLimitAt = AdminSessionLimit.LimitOf(auth.Properties, TimeSpan.FromHours(admin.Value.MaxSessionHours)),
            });
        });
        app.MapPost("/bff/logout", (HttpContext http, ILoggerFactory loggers) =>
        {
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminSignOut(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SourceAddress, actor.SessionId);
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization();
        // Extends within the absolute limit; the session (and its sign-in time) stays the same, only its expiry moves.
        app.MapPost("/bff/session/extend", async (HttpContext http, TimeProvider time, IOptions<AdminOptions> admin, ILoggerFactory loggers) =>
        {
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = auth.Properties ?? new AuthenticationProperties();
            var maxSession = TimeSpan.FromHours(admin.Value.MaxSessionHours);
            if (AdminSessionLimit.Extend(properties, maxSession, time.GetUtcNow()) is not { } expires)
            {
                return Results.Problem(statusCode: 409, title: "The session cannot be extended",
                    detail: $"Sessions last at most {admin.Value.MaxSessionHours} hours. Save your work and sign in again.");
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, http.User, properties);
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminSessionExtended(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SessionId, expires, actor.SourceAddress);
            return Results.Ok(new { sessionExpiresAt = expires, sessionLimitAt = AdminSessionLimit.LimitOf(properties, maxSession) });
        }).RequireAuthorization();
    }
}
