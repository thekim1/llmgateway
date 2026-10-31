using Scalar.AspNetCore;
using Ume.LlmGateway.DataApi;
using Ume.LlmGateway.DataApi.Authentication;
using Ume.LlmGateway.DataApi.Feeds;
using Ume.LlmGateway.Infrastructure;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.AddDataApiServices();
builder.AddUmeWebDefaults(maxRequestBodySize: 16 * 1024);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "LLM Gateway Data API";
    doc.Info.Description = "Read-only usage, security and catalogue data for BI, management and security teams. "
        + "Authenticate with an OAuth 2.0 client-credentials access token or a Data API key (Authorization: Bearer ume-dk-..., or X-Api-Key). Feeds are incremental: pass nextCursor as 'after'. "
        + "Add format=csv or format=ndjson (or the matching Accept header) for bulk loads. See docs/data-access.md.";
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseUmeWebDefaults();
app.UseUmeSecurityHeaders(o => o.NoStorePrefixes.Add("/v1"));
app.UseApiReferencePolicy("/scalar", ScalarOptions.NonceHttpContextItemKey);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use((http, next) => DataApiAuthentication.LogReadsAsync(http, () => next(http)));
app.MapDefaultEndpoints();
if (app.Configuration.GetValue($"{DataApiOptions.SectionName}:{nameof(DataApiOptions.PublishApiReference)}", true))
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("LLM Gateway Data API").WithNonce().DisableDefaultFonts());
    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}
app.MapGroup("/v1").RequireRateLimiting(DataApiServices.PerClientRateLimit).MapDataEndpoints();

await app.RunAsync();

public partial class Program;
