using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 1 || args[0] != "--health-check")
        {
            return false;
        }

        Environment.ExitCode = await RunHealthProbeAsync();
        return true;
    }

    private static async Task<int> RunHealthProbeAsync()
    {
        // Runs inside the container, so this is the container's own listener (the Kestrel URL in Compose),
        // not the host port published to the network. Override only if that internal URL changes.
        var url = Environment.GetEnvironmentVariable("UME_HEALTHCHECK_URL") ?? "https://localhost:8443" + HealthEndpoints.ReadyPath;
        // The loopback name need not be in the certificate (certificates issued for the public name),
        // so a name mismatch is accepted; an untrusted or expired certificate still fails the probe.
        using var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, errors) =>
                errors is System.Net.Security.SslPolicyErrors.None or System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(new Uri(url));
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

        builder.WebHost.ConfigureKestrel(o => o.UseUmeDefaults(maxRequestBodySize));
        return builder;
    }

    /// <summary>No <c>Server</c> header, and a request body limit.</summary>
    public static KestrelServerOptions UseUmeDefaults(this KestrelServerOptions options, long maxRequestBodySize)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddServerHeader = false;
        options.Limits.MaxRequestBodySize = maxRequestBodySize;
        return options;
    }

    /// <summary>The exception handler registered by <see cref="AddUmeWebDefaults"/>, and HSTS outside development.</summary>
    public static WebApplication UseUmeWebDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        return app.UseUmeHsts();
    }

    /// <summary>HSTS outside development.</summary>
    public static WebApplication UseUmeHsts(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        return app;
    }

    /// <summary>
    /// Sets the security headers on every response. They are applied again when the response starts, so responses
    /// written by the exception handler (which clears the headers) carry them too; a header set by an endpoint is kept.
    /// The header sets are built once, so a request costs only the path checks (the gateway runs this on its hot path).
    /// </summary>
    public static IApplicationBuilder UseUmeSecurityHeaders(this IApplicationBuilder app, Action<SecurityHeaderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new SecurityHeaderOptions();
        configure?.Invoke(options);
        var headers = new SecurityHeaderSets(options);
        return app.Use((http, next) =>
        {
            foreach (var (name, value) in headers.For(http.Request.Path))
            {
                http.Response.Headers[name] = value;
            }

            http.Response.OnStarting(headers.ReapplyOnStarting, http);
            return next(http);
        });
    }

    /// <summary>The four possible header sets (with or without CSP, with or without <c>no-store</c>), built from the options once.</summary>
    private sealed class SecurityHeaderSets
    {
        private readonly KeyValuePair<string, string>[][] _sets = new KeyValuePair<string, string>[4][];
        private readonly string? _csp;
        private readonly PathString[] _cspPrefixes;
        private readonly PathString[] _noStorePrefixes;

        public SecurityHeaderSets(SecurityHeaderOptions options)
        {
            _csp = options.ContentSecurityPolicy;
            _cspPrefixes = [.. options.ContentSecurityPolicyPrefixes];
            _noStorePrefixes = [.. options.NoStorePrefixes];
            KeyValuePair<string, string>[] common =
            [
                new("X-Content-Type-Options", "nosniff"),
                new("X-Frame-Options", options.FrameOptions),
                new("Referrer-Policy", options.ReferrerPolicy),
            ];
            for (var set = 0; set < _sets.Length; set++)
            {
                var csp = (set & 1) != 0 && _csp is not null ? new KeyValuePair<string, string>[] { new("Content-Security-Policy", _csp) } : [];
                var noStore = (set & 2) != 0 ? new KeyValuePair<string, string>[] { new("Cache-Control", "no-store") } : [];
                _sets[set] = [.. common, .. csp, .. noStore];
            }

            ReapplyOnStarting = state =>
            {
                var http = (HttpContext)state;
                foreach (var (name, value) in For(http.Request.Path))
                {
                    http.Response.Headers.TryAdd(name, value);
                }

                return Task.CompletedTask;
            };
        }

        /// <summary>Adds the headers an endpoint or the exception handler left out; the state is the <see cref="HttpContext"/>.</summary>
        public Func<object, Task> ReapplyOnStarting { get; }

        public KeyValuePair<string, string>[] For(PathString path)
        {
            var csp = _csp is not null && (_cspPrefixes.Length == 0 || StartsWithAny(path, _cspPrefixes)) ? 1 : 0;
            var noStore = StartsWithAny(path, _noStorePrefixes) ? 2 : 0;
            return _sets[csp | noStore];
        }

        private static bool StartsWithAny(PathString path, PathString[] prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (path.StartsWithSegments(prefix))
                {
                    return true;
                }
            }

            return false;
        }
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
