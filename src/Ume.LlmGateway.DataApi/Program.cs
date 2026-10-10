using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Scalar.AspNetCore;
using Ume.LlmGateway.DataApi;
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
builder.Services.AddOptions<DataApiOptions>()
    .BindConfiguration(DataApiOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "DataApi:TimeZone is not a known time zone id.")
    .ValidateOnStart();
builder.AddDataApiAuthentication();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
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
            PermitLimit = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<DataApiOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, detail: "Too many requests for this client. Wait a minute.").ExecuteAsync(context.HttpContext);
    };
});
builder.WebHost.ConfigureKestrel(o => { o.AddServerHeader = false; o.Limits.MaxRequestBodySize = 16 * 1024; });

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async http =>
{
    var exception = http.Features.Get<IExceptionHandlerFeature>()!.Error;
    if (exception is BadHttpRequestException bad)
    {
        await Results.Problem(statusCode: bad.StatusCode, detail: bad.Message).ExecuteAsync(http);
        return;
    }

    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DataApi")
        .LogError("Data API request failed ({ExceptionType})", exception.GetType().Name);
    await Results.Problem(statusCode: 500, detail: "Internal error.").ExecuteAsync(http);
}));
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (http.Request.Path.StartsWithSegments("/v1"))
    {
        http.Response.Headers.CacheControl = "no-store";
    }

    await next(http);
});
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
