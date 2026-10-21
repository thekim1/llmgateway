using System.Threading.RateLimiting;
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
builder.Services.AddSingleton<ReleaseCheck>();
builder.Services.AddSingleton<AdminClientAddresses>();
builder.Services.AddSingleton<AccessDeniedThrottle>();
builder.Services.AddHttpClient(ReleaseCheck.HttpClientName);
builder.Services.AddHttpClient("gateway-operations").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// One attempt within discovery's own 20 s timeout: an admin testing a connection should not sit through retries.
builder.Services.AddHttpClient(ModelDiscovery.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    .RemoveStandardResilienceHandler();
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy(AdminPolicies.PerUserRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(AdminClaims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.RejectWithProblem("Too many requests. Wait a minute.");
});
builder.AddAdminAuthentication();
var app = builder.Build();
app.UseUmeWebDefaults();
app.UseUmeSecurityHeaders(o =>
{
    o.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    o.NoStorePrefixes.Add("/api");
    o.NoStorePrefixes.Add("/bff");
});
app.UseApiReferencePolicy("/scalar", ScalarOptions.NonceHttpContextItemKey);
app.UseProblemDetailsForEmptyErrors("The action could not be completed");
// Outside authentication and authorization, so it sees their 403s and the signed-in user afterwards.
app.UseMiddleware<AdminActivityMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<AdminAntiforgeryMiddleware>();
app.MapDefaultEndpoints();
app.MapAdminAuthentication();
var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting(AdminPolicies.PerUserRateLimit).AddEndpointFilter<AdminValidationFilter>()
    .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
api.MapOrganisation();
api.MapKeys();
api.MapConfiguration();
api.MapSettings();
api.MapRoutingRules();
api.MapReports();
api.MapOperations();
api.MapDataAccess();
app.MapOpenApi().RequireAuthorization(AdminPolicies.Admin);
app.MapScalarApiReference(o => o.WithNonce().DisableDefaultFonts()).RequireAuthorization(AdminPolicies.Admin);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;
