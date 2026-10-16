namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// Dedicated HTTP client for upstream LLM providers. Deliberately NOT built through the default
/// HttpClientFactory pipeline: the standard resilience handler's total timeout (30 s) and retries would break
/// long streaming responses and double-bill requests. Timeouts, fallback and circuit breaking are handled by
/// the gateway pipeline instead.
/// </summary>
public sealed class ProviderHttpClient : IDisposable
{
    public ProviderHttpClient(bool requireHttps = false)
        : this(CreateHandler(System.Net.DecompressionMethods.All), CreateHandler(System.Net.DecompressionMethods.None), requireHttps)
    {
    }

    public ProviderHttpClient(HttpMessageHandler handler, bool requireHttps = false)
        : this(handler, null, requireHttps)
    {
    }

    private ProviderHttpClient(HttpMessageHandler handler, HttpMessageHandler? streamHandler, bool requireHttps)
    {
        RequireHttps = requireHttps;
        Client = Create(handler);
        StreamClient = streamHandler is null ? Client : Create(streamHandler);
    }

    public HttpClient Client { get; }

    /// <summary>
    /// For streamed calls: sends no <c>Accept-Encoding</c>. A provider or proxy that compresses <c>text/event-stream</c>
    /// holds tokens back until a compression block fills, so the client sees bursts separated by silences.
    /// </summary>
    public HttpClient StreamClient { get; }

    internal bool RequireHttps { get; }

    /// <summary>Whether content and credentials may be sent to <paramref name="uri"/> (production: only over TLS).</summary>
    internal bool Permits(Uri uri) => ProviderTransport.IsPermitted(uri, RequireHttps);

    public void Dispose()
    {
        Client.Dispose();
        if (!ReferenceEquals(StreamClient, Client))
        {
            StreamClient.Dispose();
        }
    }

    private static SocketsHttpHandler CreateHandler(System.Net.DecompressionMethods decompression) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = decompression,
        AllowAutoRedirect = false,
    };

    private static HttpClient Create(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(ProviderTransport.UserAgent);
        return client;
    }
}
