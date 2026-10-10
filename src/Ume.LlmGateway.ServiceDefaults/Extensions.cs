using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Ume.LlmGateway.ServiceDefaults;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Shared Aspire service defaults: service discovery, resilience, health checks and OpenTelemetry.
/// Privacy: no request/response bodies or auth headers are ever added to telemetry.
/// </summary>
public static class Extensions
{
    public const string TelemetryName = "Ume.LlmGateway";

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

    /// <summary>
    /// Takes the standard resilience handler (retries, 30 s total timeout) that <see cref="AddServiceDefaults{TBuilder}"/> adds to
    /// every client out of this client's pipeline, for calls that must not be repeated or that have their own timeout.
    /// (RemoveAllResilienceHandlers is still experimental.)
    /// </summary>
    public static IHttpClientBuilder RemoveStandardResilienceHandler(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureAdditionalHttpMessageHandlers(static (handlers, _) =>
        {
            for (var i = handlers.Count - 1; i >= 0; i--)
            {
                if (handlers[i] is Microsoft.Extensions.Http.Resilience.ResilienceHandler)
                {
                    handlers.RemoveAt(i);
                }
            }
        });
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
    /// aggregate status (no exception details), so they are safe to expose to orchestrators/load balancers.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpoints.LivePath, new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") })
            .DisableHttpMetrics();
        app.MapHealthChecks(HealthEndpoints.ReadyPath).DisableHttpMetrics();
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
