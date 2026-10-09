using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Scalar.AspNetCore;
using Ume.LlmGateway.AdminApi;
using Ume.LlmGateway.Infrastructure;

if (await Extensions.RunHealthProbeAsync(args) is { } probeExit)
{
    Environment.ExitCode = probeExit;
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.AddGatewaySecurity();
builder.AddGatewayStores();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddScoped<AdminContext>();
builder.Services.AddSingleton<GatewayOperationsClient>();
builder.Services.AddHttpClient("gateway-operations").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("provider-discovery").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) });
var requestsPerMinute = builder.Configuration.GetValue<int?>("Admin:RequestsPerMinute") ?? 120;
if (requestsPerMinute is < 1 or > 10000) { throw new InvalidOperationException("Admin:RequestsPerMinute must be between 1 and 10000."); }
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("admin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, detail: "För många anrop. Vänta en minut.").ExecuteAsync(context.HttpContext);
    };
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
    o.Authority = builder.Configuration["Oidc:Authority"];
    o.ClientId = builder.Configuration["Oidc:ClientId"] ?? "ume-admin";
    o.ClientSecret = builder.Configuration["Oidc:ClientSecret"];
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
        AdminAuthentication.ExpandArrayClaims(ctx.Principal);
        return Task.CompletedTask;
    };
    // An expired session on an API call must answer 401 so the UI can send the user to sign in; a redirect to the
    // identity provider would be followed by fetch and fail on CORS. Only /bff/login starts a real sign-in.
    o.Events.OnRedirectToIdentityProvider = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
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
    o.AddPolicy("admin", p => p.RequireRole("gateway-admin"));
    o.AddPolicy("manage", p => p.RequireRole("gateway-admin", "department-admin"));
    o.AddPolicy("read", p => p.RequireRole("gateway-admin", "department-admin", "viewer"));
});
builder.WebHost.ConfigureKestrel(o => { o.AddServerHeader = false; o.Limits.MaxRequestBodySize = 1024 * 1024; });
var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async http =>
{
    var exception = http.Features.Get<IExceptionHandlerFeature>()!.Error;
    var status = exception is AdminFaultException fault ? fault.Status :
        exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException or Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: PostgresException { SqlState: "23505" or "23503" } } ? 409 : 500;
    var detail = exception is AdminFaultException known ? known.Message : status == 409 ? "Ändringen krockar med befintliga uppgifter." : "Ett internt fel inträffade.";
    if (status == 500)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AdminApi")
            .LogError("Admin request failed ({ExceptionType})", exception.GetType().Name);
    }
    await Results.Problem(statusCode: status, title: "Åtgärden kunde inte utföras", detail: detail,
        extensions: (exception as AdminFaultException)?.Extensions).ExecuteAsync(http);
}));
app.Use(async (http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.XFrameOptions = "DENY";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/bff"))
    {
        http.Response.Headers.CacheControl = "no-store";
    }
    await next(http);
    if (http.Response.StatusCode >= 400 && !http.Response.HasStarted)
    {
        await Results.Problem(statusCode: http.Response.StatusCode, title: "Åtgärden kunde inte utföras").ExecuteAsync(http);
    }
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<AdminAntiforgeryMiddleware>();
app.MapDefaultEndpoints();
app.MapAdminAuthentication();
var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("admin").AddEndpointFilter<AdminValidationFilter>();
api.MapOrganisation();
api.MapKeys();
api.MapConfiguration();
api.MapRoutingRules();
api.MapReports();
api.MapOperations();
app.MapOpenApi().RequireAuthorization("admin");
app.MapScalarApiReference().RequireAuthorization("admin");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;
