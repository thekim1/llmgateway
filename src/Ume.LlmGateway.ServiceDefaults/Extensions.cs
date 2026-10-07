using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Shared Aspire service defaults: service discovery, resilience, health checks and OpenTelemetry.
/// Privacy: no request/response bodies or auth headers are ever added to telemetry.
/// </summary>
public static class Extensions
{
    public const string TelemetryName = "Ume.LlmGateway";
    private const string LivePath = "/health/live";
    private const string ReadyPath = "/health/ready";

    public static async Task<int?> RunHealthProbeAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--health-check")
        {
            return null;
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync("https://localhost:8443/health/ready");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });
        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(TelemetryName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(TelemetryName)
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    // Never record query strings; they could contain identifiers.
                    o.EnrichWithHttpRequest = (activity, request) => activity.SetTag("url.query", null);
                })
                .AddHttpClientInstrumentation(o =>
                    o.EnrichWithHttpRequestMessage = (activity, request) => activity.SetTag("url.query", null)));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    /// <summary>
    /// Maps /health/live (process up) and /health/ready (dependencies OK). Responses contain only the
    /// aggregate status � no exception details � so they are safe to expose to orchestrators/load balancers.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") })
            .DisableHttpMetrics();
        app.MapHealthChecks(ReadyPath).DisableHttpMetrics();
        app.MapGet("/version", () => Results.Ok(new
        {
            application = app.Environment.ApplicationName,
            version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            informationalVersion = System.Reflection.Assembly.GetEntryAssembly()?
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
        })).ExcludeFromDescription();
        return app;
    }
}
