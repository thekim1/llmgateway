using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure;
using Ume.LlmGateway.Infrastructure.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
builder.Services.AddProviderAdapters();

builder.Services.AddOptions<GatewayOptions>()
    .BindConfiguration(GatewayOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<KeyAuthenticator>();
builder.Services.AddSingleton<GatewayCatalog>();
builder.Services.AddSingleton<BudgetService>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<UsageWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageWriter>());
builder.Services.AddSingleton<GatewayRequestHandler>();
builder.Services.AddHttpClient("alerts");
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Umeå kommun LLM Gateway";
    doc.Info.Description = "OpenAI- och Anthropic-kompatibelt API. Autentisera med en virtuell nyckel: `Authorization: Bearer ume-sk-…`.";
    return Task.CompletedTask;
}));

var maxBody = builder.Configuration.GetValue<long?>($"{GatewayOptions.SectionName}:{nameof(GatewayOptions.MaxRequestBodyBytes)}") ?? new GatewayOptions().MaxRequestBodyBytes;
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = maxBody;
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseMiddleware<RequestIdMiddleware>();
app.Use(async (http, next) =>
{
    var headers = http.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.XFrameOptions = "DENY";
    if (http.Request.Path.StartsWithSegments("/v1"))
    {
        headers.CacheControl = "no-store";
        headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    }

    await next(http);
});

app.MapDefaultEndpoints();
app.MapGet(OperationsSignature.Path, async (HttpContext http, IOptions<GatewaySecurityOptions> security,
    TimeProvider time, UsageWriter writer, HealthCheckService health, CancellationToken ct) =>
{
    if (!OperationsSignature.Verify(security.Value.KeyPepper, http.Request.Headers[OperationsSignature.TimestampHeader].ToString(),
        http.Request.Headers[OperationsSignature.SignatureHeader].ToString(), time.GetUtcNow()))
    {
        return Results.Unauthorized();
    }
    http.Response.Headers.CacheControl = "no-store";
    var report = await health.CheckHealthAsync(ct);
    var status = writer.Status;
    return Results.Ok(new GatewayOperationsStatus(
        status.ConsecutiveFailures > 0 && report.Status == HealthStatus.Healthy ? "Degraded" : report.Status.ToString(),
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown", status));
}).ExcludeFromDescription();
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Umeå kommun LLM Gateway"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

var v1 = app.MapGroup("/v1").WithTags("LLM");

v1.MapPost("/chat/completions", (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, GatewayEndpoint.ChatCompletions))
    .WithSummary("Chat completions (OpenAI-kompatibel, stöder stream)")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

v1.MapPost("/embeddings", (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, GatewayEndpoint.Embeddings))
    .WithSummary("Embeddings (OpenAI-kompatibel)")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

v1.MapPost("/responses", (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, GatewayEndpoint.Responses))
    .WithSummary("Responses API (OpenAI-kompatibel, endast leverantörer som stöder den)")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

v1.MapPost("/messages", (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, GatewayEndpoint.AnthropicMessages))
    .WithSummary("Anthropic Messages API (endast Anthropic-leverantörer)")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

v1.MapGet("/models", (HttpContext http, GatewayRequestHandler h) => h.ListModelsAsync(http))
    .WithSummary("Modeller och alias som nyckeln får använda")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

// Fail fast on invalid configuration (e.g. missing key pepper) with an actionable message.
_ = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;

await app.RunAsync();

public partial class Program;
