using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure;
using Ume.LlmGateway.Infrastructure.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
builder.Services.AddProviderAdapters();

builder.Services.AddOptions<GatewayOptions>()
    .BindConfiguration(GatewayOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<KeyAuthenticator>();
builder.Services.AddSingleton<GatewayCatalog>();
builder.Services.AddSingleton<IRouteResolver, RouteResolver>();
builder.Services.AddSingleton<BudgetService>();
builder.Services.AddSingleton<GatewayMetrics>();
// Registered before the usage writer so it stops after it: alerts raised while the writer drains are still sent.
builder.Services.AddSingleton<AlertNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertNotifier>());
builder.Services.AddSingleton<UsageWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageWriter>());
builder.Services.AddSingleton<AuthFailureRecorder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuthFailureRecorder>());
builder.Services.AddSingleton<GatewayRequestHandler>();
// One short attempt per alert: the standard resilience handler's retries would POST the same alert several times.
builder.Services.AddHttpClient(AlertNotifier.HttpClientName, c => c.Timeout = AlertNotifier.SendTimeout).RemoveStandardResilienceHandler();
builder.Services.AddHealthChecks().AddCheck<ExchangeRateHealthCheck>(ExchangeRateHealthCheck.Name);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Umeå kommun LLM Gateway";
    doc.Info.Description = "OpenAI- och Anthropic-kompatibelt API. Autentisera med en virtuell nyckel: `Authorization: Bearer ume-sk-…`.";
    return Task.CompletedTask;
}));

// From the validated options, so Kestrel's limit and the gateway's own body check never disagree.
builder.Services.AddOptions<KestrelServerOptions>().Configure<IOptions<GatewayOptions>>((kestrel, gateway) =>
{
    kestrel.UseUmeDefaults(gateway.Value.MaxRequestBodyBytes);
    kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

var app = builder.Build();

app.UseUmeHsts();

app.UseMiddleware<RequestIdMiddleware>();
// Only on the live audio paths: the middleware would otherwise add a handshake object to every request.
app.UseWhen(http => http.Request.Path.StartsWithSegments("/v1/realtime"),
    branch => branch.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) }));
// The client API answers are JSON for programs: nothing may frame, render or cache them.
app.UseUmeSecurityHeaders(o =>
{
    o.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    o.ContentSecurityPolicyPrefixes.Add("/v1");
    o.NoStorePrefixes.Add("/v1");
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

v1.MapPost("/audio/transcriptions", (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, GatewayEndpoint.AudioTranscriptions))
    .WithSummary("Tal till text (OpenAI-kompatibel, multipart/form-data med fältet 'file')")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

v1.MapPost("/audio/translations", (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, GatewayEndpoint.AudioTranslations))
    .WithSummary("Tal till engelsk text (OpenAI-kompatibel, multipart/form-data med fältet 'file')")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

// Live audio: WebSocket (OpenAI Realtime protocol). HTTP/2 extended CONNECT is presented as GET as well.
v1.MapGet("/realtime", (HttpContext http, GatewayRequestHandler h) => h.HandleRealtimeAsync(http, GatewayEndpoint.Realtime))
    .WithSummary("Realtidsljud över WebSocket: live-transkribering och realtidssamtal (OpenAI Realtime-kompatibel)");

v1.MapGet("/realtime/translations", (HttpContext http, GatewayRequestHandler h) => h.HandleRealtimeAsync(http, GatewayEndpoint.RealtimeTranslations))
    .WithSummary("Live-tolkning över WebSocket (OpenAI Realtime translations-kompatibel)");

v1.MapGet("/models", (HttpContext http, GatewayRequestHandler h) => h.ListModelsAsync(http))
    .WithSummary("Modeller och alias som nyckeln får använda")
    .Produces<System.Text.Json.Nodes.JsonObject>(200);

await app.RunAsync();

public partial class Program;
