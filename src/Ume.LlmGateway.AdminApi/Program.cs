using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Scalar.AspNetCore;
using Ume.LlmGateway.AdminApi;
using Ume.LlmGateway.Infrastructure;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.AddGatewaySecurity();
builder.AddGatewayStores();
builder.AddUmeWebDefaults(maxRequestBodySize: 1024 * 1024, errors =>
{
    errors.Title = "The action could not be completed";
    // Concurrent edits and unique or foreign key violations are the caller's to resolve, not internal errors.
    errors.Translators.Add(exception => exception is DbUpdateConcurrencyException or DbUpdateException { InnerException: PostgresException { SqlState: "23505" or "23503" } }
        ? new ApiFaultException(StatusCodes.Status409Conflict, "The change conflicts with existing data.")
        : null);
});
builder.Services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<GatewayLinkOptions>().BindConfiguration(GatewayLinkOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<DataApiLinkOptions>().BindConfiguration(DataApiLinkOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOpenApi();
builder.Services.AddAdminServices();
builder.Services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.AddSingleton<GatewayOperationsClient>();
builder.Services.AddSingleton<AzureRetailPrices>();
builder.Services.AddHttpClient(AzureRetailPrices.HttpClientName);
builder.Services.AddHttpClient("gateway-operations").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// One attempt within discovery's own 20 s timeout: an admin testing a connection should not sit through retries.
builder.Services.AddHttpClient(ModelDiscovery.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    .RemoveStandardResilienceHandler();
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("admin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(AdminClaims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.RejectWithProblem("Too many requests. Wait a minute.");
});
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
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = false;
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
    o.TokenValidationParameters.RoleClaimType = "roles";
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
    o.AddPolicy("admin", p => p.RequireRole(AdminRoles.GatewayAdmin));
    o.AddPolicy("manage", p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin));
    o.AddPolicy("read", p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.Viewer));
});
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
var app = builder.Build();
app.UseUmeWebDefaults();
app.UseUmeSecurityHeaders(o =>
{
    o.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    o.NoStorePrefixes.Add("/api");
    o.NoStorePrefixes.Add("/bff");
});
app.UseProblemDetailsForEmptyErrors("The action could not be completed");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<AdminAntiforgeryMiddleware>();
app.MapDefaultEndpoints();
app.MapAdminAuthentication();
var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("admin").AddEndpointFilter<AdminValidationFilter>()
    .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
api.MapOrganisation();
api.MapKeys();
api.MapConfiguration();
api.MapSettings();
api.MapRoutingRules();
api.MapReports();
api.MapOperations();
api.MapDataAccess();
app.MapOpenApi().RequireAuthorization("admin");
app.MapScalarApiReference().RequireAuthorization("admin");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;
