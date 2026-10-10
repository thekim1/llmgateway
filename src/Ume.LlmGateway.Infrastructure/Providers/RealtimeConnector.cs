using System.Net.WebSockets;
using System.Text;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// One attempt to open a realtime session upstream. <see cref="Query"/> holds the client's query parameters other than
/// <c>model</c> (e.g. <c>intent</c>), <see cref="Headers"/> the client headers passed on (e.g. <c>OpenAI-Beta</c>).
/// </summary>
public sealed record RealtimeCall(
    ProviderAccount Provider,
    ModelDeployment Deployment,
    GatewayEndpoint Endpoint,
    string? Credential,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>Opens the upstream WebSocket of a realtime session (OpenAI Realtime protocol).</summary>
public interface IRealtimeConnector
{
    /// <summary>
    /// The open socket, or a failure (its <see cref="ProviderFailure.Kind"/> decides fallback). Caller cancellation propagates as
    /// <see cref="OperationCanceledException"/>; a provider that does not answer in time is a transient 504.
    /// </summary>
    Task<(WebSocket? Socket, ProviderFailure? Failure)> ConnectAsync(RealtimeCall call, CancellationToken cancellationToken);
}

/// <summary>
/// OpenAI, Azure OpenAI and Azure AI Foundry (<c>…/openai/v1</c>), Foundry Voice Live (<c>…/voice-live?api-version=…</c>),
/// and on-prem servers implementing the same protocol (vLLM, Speaches): the session URL is the provider's base URL plus
/// <c>/realtime</c> or <c>/realtime/translations</c>, with the upstream model as <c>model</c>.
/// </summary>
public sealed class RealtimeConnector(ProviderHttpClient http) : IRealtimeConnector
{
    /// <summary>Query parameters never passed on from the client: the gateway sets the model, and credentials stay out of URLs.</summary>
    private static readonly HashSet<string> ReservedQuery = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "api-key", "api_key", "key", "access_token", "token", "authorization", "deployment",
    };

    public async Task<(WebSocket? Socket, ProviderFailure? Failure)> ConnectAsync(RealtimeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Provider.Type == ProviderType.Anthropic)
        {
            return (null, ProviderFailure.EndpointNotSupported);
        }

        var uri = BuildUri(call.Provider.BaseUrl, call.Endpoint, call.Deployment.UpstreamModel, call.Query);
        if (!http.Permits(uri))
        {
            return (null, ProviderFailure.HttpsRequired);
        }

        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        foreach (var (name, value) in call.Headers)
        {
            socket.Options.SetRequestHeader(name, value);
        }

        ProviderAuth.Apply(socket.Options, call.Provider, call.Credential);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProviderTransport.RealtimeConnectTimeout(call.Provider));
        try
        {
            // The shared handler: same connection limits and TLS settings as the HTTP calls.
            await socket.ConnectAsync(uri, http.Client, timeout.Token);
            return (socket, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return (null, ProviderFailure.Timeout);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            var status = (int)socket.HttpStatusCode;
            socket.Dispose();
            return status is 0 or 101
                ? (null, new ProviderFailure(502, FailureKind.Transient, $"connection_error:{(ex as WebSocketException)?.WebSocketErrorCode.ToString() ?? "http"}"))
                : (null, ProviderFailure.FromStatus(status));
        }
    }

    /// <summary>
    /// <c>{base path}/realtime[/translations]?{base query}&amp;{client query}&amp;model={upstream}</c> with <c>ws(s)</c>.
    /// Parameters on the base URL win over the client's. With <c>intent</c> (OpenAI's transcription sessions) the model
    /// is set in <c>session.update</c> instead of the URL.
    /// </summary>
    public static Uri BuildUri(string baseUrl, GatewayEndpoint endpoint, string upstreamModel, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var http = ProviderTransport.BuildUri(baseUrl, ProviderTransport.UpstreamPath(endpoint));
        var builder = new UriBuilder(http) { Scheme = http.Scheme == Uri.UriSchemeHttp ? "ws" : "wss", Port = http.IsDefaultPort ? -1 : http.Port };
        var parts = new StringBuilder(http.Query.TrimStart('?'));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in http.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            taken.Add(Uri.UnescapeDataString(pair.Split('=')[0]));
        }

        void Add(string name, string value)
        {
            if (taken.Add(name))
            {
                parts.Append(parts.Length > 0 ? "&" : string.Empty).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        foreach (var (name, value) in query)
        {
            if (!ReservedQuery.Contains(name))
            {
                Add(name, value);
            }
        }

        if (!query.Any(q => q.Key.Equals("intent", StringComparison.OrdinalIgnoreCase)))
        {
            Add("model", upstreamModel);
        }

        builder.Query = parts.ToString();
        return builder.Uri;
    }
}
