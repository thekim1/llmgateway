using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// How a provider expects to be authenticated, in one place for every way the gateway talks to providers (HTTP calls,
/// realtime WebSockets, model discovery). Credentials are only ever put in headers, never in URLs.
/// </summary>
public static class ProviderAuth
{
    public const string AnthropicVersionHeader = "anthropic-version";

    /// <summary>
    /// Calls <paramref name="set"/> with each header the provider needs: the credential in the header its
    /// <see cref="ProviderAuthMode"/> names, and Anthropic's API version. <paramref name="state"/> keeps the callback static.
    /// </summary>
    public static void Apply<TState>(ProviderAccount provider, string? credential, TState state, Action<TState, string, string> set)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(set);
        if (!string.IsNullOrEmpty(credential))
        {
            switch (provider.AuthMode)
            {
                case ProviderAuthMode.Bearer:
                    set(state, "Authorization", "Bearer " + credential);
                    break;
                case ProviderAuthMode.ApiKeyHeader:
                    set(state, "api-key", credential);
                    break;
                case ProviderAuthMode.XApiKeyHeader:
                    set(state, "x-api-key", credential);
                    break;
            }
        }

        if (provider.Type == ProviderType.Anthropic)
        {
            set(state, AnthropicVersionHeader, AnthropicAdapter.ApiVersion);
        }
    }

    public static void Apply(HttpRequestMessage request, ProviderAccount provider, string? credential)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.AuthMode == ProviderAuthMode.Bearer && !string.IsNullOrEmpty(credential))
        {
            // The typed header: no "Bearer " string to build and parse again.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            credential = null;
        }

        Apply(provider, credential, request.Headers, static (headers, name, value) => headers.TryAddWithoutValidation(name, value));
    }

    public static void Apply(ClientWebSocketOptions options, ProviderAccount provider, string? credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        Apply(provider, credential, options, static (o, name, value) => o.SetRequestHeader(name, value));
    }
}

/// <summary>Transport rules shared by the HTTP adapters and the realtime connector.</summary>
public static class ProviderTransport
{
    /// <summary>Upper bound of a provider's <see cref="ProviderAccount.TimeoutSeconds"/> for an HTTP call (headers, or the whole JSON answer).</summary>
    public const int MaxRequestTimeoutSeconds = 900;

    /// <summary>Upper bound for opening a realtime WebSocket: a handshake that takes longer is not going to work.</summary>
    public const int MaxRealtimeConnectTimeoutSeconds = 120;

    /// <summary><c>Ume-LlmGateway/{version}</c>: the assembly's informational version, without build metadata.</summary>
    public static ProductInfoHeaderValue UserAgent { get; } = new("Ume-LlmGateway", InformationalVersion());

    public static TimeSpan RequestTimeout(ProviderAccount provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, MaxRequestTimeoutSeconds));
    }

    public static TimeSpan RealtimeConnectTimeout(ProviderAccount provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, MaxRealtimeConnectTimeoutSeconds));
    }

    /// <summary>Whether <paramref name="uri"/> may be used: with <paramref name="requireHttps"/> (production) only <c>https</c> and <c>wss</c>.</summary>
    public static bool IsPermitted(Uri uri, bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return !requireHttps || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeWss;
    }

    /// <summary>
    /// <paramref name="path"/> appended to the base URL's path. A query string on the base URL (e.g. Azure's
    /// <c>api-version</c>) is kept after it.
    /// </summary>
    public static Uri BuildUri(string baseUrl, string path)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var query = baseUrl.IndexOf('?', StringComparison.Ordinal);
        return query < 0
            ? new Uri(baseUrl.TrimEnd('/') + "/" + path)
            : new Uri(baseUrl[..query].TrimEnd('/') + "/" + path + baseUrl[query..]);
    }

    /// <summary>The provider path of <paramref name="endpoint"/> (see <see cref="GatewayEndpoints"/>).</summary>
    public static string UpstreamPath(GatewayEndpoint endpoint) =>
        GatewayEndpoints.Info(endpoint).UpstreamPath ?? throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "The endpoint is never sent to a provider.");

    private static string InformationalVersion()
    {
        var version = typeof(ProviderTransport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ProviderTransport).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}
