using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Ume.LlmGateway.DataApi;
using Ume.LlmGateway.Infrastructure;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.Services.AddOptions<DataApiOptions>()
    .BindConfiguration(DataApiOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "DataApi:TimeZone is not a known time zone id.")
    .ValidateOnStart();
builder.AddDataApiAuthentication();
builder.AddUmeWebDefaults(maxRequestBodySize: 16 * 1024);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "LLM Gateway Data API";
    doc.Info.Description = "Read-only usage, security and catalogue data for BI, management and security teams. "
        + "Authenticate with an OAuth 2.0 client-credentials access token. Feeds are incremental: pass nextCursor as 'after'. "
        + "Add format=csv or format=ndjson (or the matching Accept header) for bulk loads. See docs/data-access.md.";
    return Task.CompletedTask;
}));
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("client", http => RateLimitPartition.GetFixedWindowLimiter(DataApiAuthentication.ClientId(http.User), _ =>
        new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<DataApiOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.RejectWithProblem("Too many requests for this client. Wait a minute.");
});

var app = builder.Build();
app.UseUmeWebDefaults();
app.UseUmeSecurityHeaders(o => o.NoStorePrefixes.Add("/v1"));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use((http, next) => DataApiAuthentication.LogReadsAsync(http, () => next(http)));
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("LLM Gateway Data API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
app.MapGroup("/v1").RequireRateLimiting("client").MapDataEndpoints();

await app.RunAsync();

public partial class Program;
