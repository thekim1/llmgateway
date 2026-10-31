using Microsoft.EntityFrameworkCore;
using Npgsql;
using Scalar.AspNetCore;
using Ume.LlmGateway.AdminApi;
using Ume.LlmGateway.AdminApi.Security;
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
builder.Services.AddOpenApi();
builder.Services.AddAdminServices();
builder.Services.AddAdminRateLimiting();
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
app.MapAdminApi();
app.MapOpenApi().RequireAuthorization(AdminPolicies.Admin);
app.MapScalarApiReference(o => o.WithNonce().DisableDefaultFonts()).RequireAuthorization(AdminPolicies.Admin);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;
