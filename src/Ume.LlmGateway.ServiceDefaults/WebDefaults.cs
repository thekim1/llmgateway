using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.ServiceDefaults;

namespace Microsoft.Extensions.Hosting;

/// <summary>Response headers every HTTP host sets. Defaults: <c>nosniff</c>, <c>X-Frame-Options: DENY</c>, <c>Referrer-Policy: no-referrer</c>.</summary>
public sealed class SecurityHeaderOptions
{
    public string FrameOptions { get; set; } = "DENY";
    public string ReferrerPolicy { get; set; } = "no-referrer";

    /// <summary><c>Content-Security-Policy</c>; <c>null</c> sends none.</summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>Paths the CSP applies to; empty applies it to every response.</summary>
    public IList<PathString> ContentSecurityPolicyPrefixes { get; } = [];

    /// <summary>Paths answered with <c>Cache-Control: no-store</c> (API responses that may hold personal or secret data).</summary>
    public IList<PathString> NoStorePrefixes { get; } = [];
}

/// <summary>Shared web host setup: JSON, error responses, Kestrel, HSTS, security headers and rate-limit rejections.</summary>
public static class WebDefaults
{
    /// <summary>
    /// Runs the container health probe when started with <c>--health-check</c> and sets the exit code. Returns
    /// <c>true</c> when the probe ran; the caller then returns without starting the host.
    /// </summary>
    public static async Task<bool> ExitIfHealthProbeAsync(string[] args)
    {
        if (await Extensions.RunHealthProbeAsync(args) is not { } exitCode)
        {
            return false;
        }

        Environment.ExitCode = exitCode;
        return true;
    }

    /// <summary>
    /// Enums as strings (numbers rejected), problem details, <see cref="ApiFaultException"/> handling, no Server header and
    /// a request body limit. Pair with <see cref="UseUmeWebDefaults"/>.
    /// </summary>
    public static WebApplicationBuilder AddUmeWebDefaults(this WebApplicationBuilder builder, long maxRequestBodySize, Action<ApiExceptionOptions>? configureErrors = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        if (configureErrors is not null)
        {
            builder.Services.Configure(configureErrors);
        }

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = maxRequestBodySize;
        });
        return builder;
    }

    /// <summary>The exception handler registered by <see cref="AddUmeWebDefaults"/>, and HSTS outside development.</summary>
    public static WebApplication UseUmeWebDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        return app;
    }

    /// <summary>
    /// Sets the security headers on every response. They are applied again when the response starts, so responses
    /// written by the exception handler (which clears the headers) carry them too; a header set by an endpoint is kept.
    /// </summary>
    public static IApplicationBuilder UseUmeSecurityHeaders(this IApplicationBuilder app, Action<SecurityHeaderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new SecurityHeaderOptions();
        configure?.Invoke(options);
        return app.Use(async (http, next) =>
        {
            var headers = Headers(http.Request.Path, options);
            foreach (var (name, value) in headers)
            {
                http.Response.Headers[name] = value;
            }

            http.Response.OnStarting(() =>
            {
                foreach (var (name, value) in headers)
                {
                    http.Response.Headers.TryAdd(name, value);
                }

                return Task.CompletedTask;
            });
            await next(http);
        });
    }

    private static List<KeyValuePair<string, string>> Headers(PathString path, SecurityHeaderOptions options)
    {
        var headers = new List<KeyValuePair<string, string>>(5)
        {
            new("X-Content-Type-Options", "nosniff"),
            new("X-Frame-Options", options.FrameOptions),
            new("Referrer-Policy", options.ReferrerPolicy),
        };
        if (options.ContentSecurityPolicy is { } csp &&
            (options.ContentSecurityPolicyPrefixes.Count == 0 || options.ContentSecurityPolicyPrefixes.Any(path.StartsWithSegments)))
        {
            headers.Add(new("Content-Security-Policy", csp));
        }

        if (options.NoStorePrefixes.Any(path.StartsWithSegments))
        {
            headers.Add(new("Cache-Control", "no-store"));
        }

        return headers;
    }

    /// <summary>Gives error responses that have no body (401 and 403 from authorization, 404 from routing) a problem body.</summary>
    public static IApplicationBuilder UseProblemDetailsForEmptyErrors(this IApplicationBuilder app, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (http, next) =>
        {
            await next(http);
            if (http.Response.StatusCode >= 400 && !http.Response.HasStarted)
            {
                await Results.Problem(statusCode: http.Response.StatusCode, title: title).ExecuteAsync(http);
            }
        });
    }

    /// <summary>Answers a rejected request with 429, <c>Retry-After</c> and a problem with <paramref name="detail"/>.</summary>
    public static RateLimiterOptions RejectWithProblem(this RateLimiterOptions options, string detail, int retryAfterSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, _) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, detail: detail).ExecuteAsync(context.HttpContext);
        };
        return options;
    }
}

/// <summary>Query-string enums: member names, case-insensitive. Numbers and combinations are rejected.</summary>
public static class EnumQuery
{
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        var text = value?.Trim();
        return !string.IsNullOrEmpty(text) && char.IsAsciiLetter(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
               Enum.TryParse(text, ignoreCase: true, out result) && Enum.IsDefined(result);
    }

    /// <summary>The value as it is written in query strings and messages: the member name in lower case.</summary>
    public static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>Every member's <see cref="Name{TEnum}"/>.</summary>
    public static string[] Names<TEnum>() where TEnum : struct, Enum => [.. Enum.GetValues<TEnum>().Select(Name)];
}
