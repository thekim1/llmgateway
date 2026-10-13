using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Infrastructure.Mcp;

/// <summary>One JSON-RPC request to an MCP server, sent on the caller's behalf.</summary>
/// <param name="Server">The server; its URL, timeout and result limit apply.</param>
/// <param name="Auth">The server's decrypted credential.</param>
/// <param name="Method">The MCP method, e.g. <c>tools/call</c>.</param>
/// <param name="Params">The request's params. Sent as given (the SDK adds its own protocol <c>_meta</c> to a copy).</param>
/// <param name="ForwardedHeaders">Caller headers the server's allow-list names; only ever sent with this request.</param>
/// <param name="MayRetry">Whether the request may be sent again after a failure that may have happened after it ran.</param>
/// <param name="Progress">
/// Receives the params of each <c>notifications/progress</c> the server sends for this request; the gateway's own
/// progress token in them is the caller's business to replace. Null: no progress is asked for.
/// </param>
public sealed record McpUpstreamCall(
    McpServer Server,
    McpUpstreamAuth Auth,
    string Method,
    JsonObject Params,
    IReadOnlyList<KeyValuePair<string, string>> ForwardedHeaders,
    bool MayRetry,
    Func<JsonObject, ValueTask>? Progress = null);

public enum McpUpstreamFailure
{
    None = 0,

    /// <summary>Connection refused or broken, or an answer that is not MCP.</summary>
    Transport = 1,

    /// <summary>No answer within the server's <see cref="McpServer.TimeoutSeconds"/>.</summary>
    Timeout = 2,

    /// <summary>HTTP 5xx.</summary>
    ServerError = 3,

    /// <summary>HTTP 401/403: the gateway's credential for the server is wrong or expired.</summary>
    Unauthorized = 4,

    /// <summary>Another HTTP 4xx (for example a wrong URL), or a URL the environment does not allow.</summary>
    Rejected = 5,

    /// <summary>The result is larger than the server's <see cref="McpServer.MaxResultBytes"/>.</summary>
    TooLarge = 6,

    /// <summary>The server answered with a JSON-RPC error (<see cref="McpUpstreamResult.ErrorCode"/>).</summary>
    JsonRpcError = 7,
}

/// <summary>
/// The answer to an <see cref="McpUpstreamCall"/>: the raw JSON-RPC <c>result</c> (unchanged, so content blocks and
/// fields the gateway does not know pass through), or why there is none.
/// </summary>
public sealed record McpUpstreamResult(JsonNode? Result, McpUpstreamFailure Failure, int Attempts, int? HttpStatus = null, int ErrorCode = 0, string? ErrorMessage = null)
{
    public bool Succeeded => Failure == McpUpstreamFailure.None;

    /// <summary>Only failures of the server itself open its circuit, not tool errors (<c>isError: true</c>) or our own configuration.</summary>
    public bool CountsAgainstCircuit => Failure is McpUpstreamFailure.Transport or McpUpstreamFailure.Timeout or McpUpstreamFailure.ServerError;
}

/// <summary>What an MCP server says about itself when a client connects, or why it could not be reached.</summary>
public sealed record McpUpstreamDescription(McpUpstreamResult Outcome, McpServerCapabilities Capabilities = McpServerCapabilities.None, string? Instructions = null);

public interface IMcpUpstream
{
    Task<McpUpstreamResult> SendAsync(McpUpstreamCall call, CancellationToken cancellationToken);
}

public sealed class McpUpstreamOptions
{
    /// <summary>Production: MCP servers only over https, like providers.</summary>
    public bool RequireHttps { get; set; }
}

/// <summary>
/// Sends MCP requests upstream through one pooled SDK client (Streamable HTTP) per server. The client is created on first
/// use and replaced when the server's URL or credential changes or after a transport failure; against a server that still
/// uses the <c>initialize</c> handshake that also re-initialises its session. Each request gets the server's timeout, and
/// is sent once more after a transport failure when <see cref="McpUpstreamCall.MayRetry"/> allows (always when the server
/// said the session was gone, since the request then never ran).
/// </summary>
public sealed class McpUpstream(IHttpMessageHandlerFactory handlers, McpTokenCache tokens, McpUpstreamOptions options) : IMcpUpstream, IAsyncDisposable
{
    public const string HttpClientName = "mcp-upstream";

    /// <summary>Headers never forwarded from callers, whatever a server's allow-list says.</summary>
    private static readonly HashSet<string> NeverForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "cookie", "host", "content-type", "content-length",
        "accept", "connection", "transfer-encoding", "mcp-session-id", "mcp-protocol-version", "mcp-method", "mcp-name",
    };

    /// <summary>Room for SSE framing and JSON escaping on top of the result limit before reading stops.</summary>
    private const int EnvelopeBytes = 64 * 1024;

    private static readonly AsyncLocal<CallContext?> Current = new();
    private readonly ConcurrentDictionary<Guid, PooledClient> _clients = new();

    /// <summary>Whether a server's allow-list may name <paramref name="header"/> at all.</summary>
    public static bool IsForwardable(string header) => !string.IsNullOrWhiteSpace(header) && !NeverForwarded.Contains(header);

    public async Task<McpUpstreamResult> SendAsync(McpUpstreamCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!Uri.TryCreate(call.Server.Url, UriKind.Absolute, out var url) || !ProviderTransport.IsPermitted(url, options.RequireHttps))
        {
            return new McpUpstreamResult(null, McpUpstreamFailure.Rejected, 0);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(call.Server.TimeoutSeconds, 1, ProviderTransport.MaxRequestTimeoutSeconds));
        McpUpstreamResult? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (result, retry) = await AttemptAsync(call, url, timeout, attempt, cancellationToken);
            if (result.Succeeded || !retry)
            {
                return result;
            }

            last = result;
        }

        return last!;
    }

    /// <summary>Connects to the server (its handshake only) and returns the capabilities and instructions it announced.</summary>
    public async Task<McpUpstreamDescription> DescribeAsync(McpServer server, McpUpstreamAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(auth);
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var url) || !ProviderTransport.IsPermitted(url, options.RequireHttps))
        {
            return new McpUpstreamDescription(new McpUpstreamResult(null, McpUpstreamFailure.Rejected, 0));
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, 1, ProviderTransport.MaxRequestTimeoutSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var pooled = Pool(server, auth, url, timeout);
        try
        {
            var client = await pooled.Client.WaitAsync(deadline.Token);
            return new McpUpstreamDescription(new McpUpstreamResult(null, McpUpstreamFailure.None, 1), Capabilities(client.ServerCapabilities), client.ServerInstructions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Evict(server.Id, pooled);
            return new McpUpstreamDescription(Classify(ex, server.Id) with { Attempts = 1 });
        }
    }

    private static McpServerCapabilities Capabilities(ServerCapabilities? capabilities) =>
        (capabilities?.Tools is null ? McpServerCapabilities.None : McpServerCapabilities.Tools)
        | (capabilities?.Resources is null ? McpServerCapabilities.None : McpServerCapabilities.Resources)
        | (capabilities?.Prompts is null ? McpServerCapabilities.None : McpServerCapabilities.Prompts)
        | (capabilities?.Completions is null ? McpServerCapabilities.None : McpServerCapabilities.Completions);

    private async Task<(McpUpstreamResult Result, bool Retry)> AttemptAsync(McpUpstreamCall call, Uri url, TimeSpan timeout, int attempt, CancellationToken cancellationToken)
    {
        var canRetry = attempt == 1;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var pooled = Pool(call.Server, call.Auth, url, timeout);
        McpClient client;
        try
        {
            client = await pooled.Client.WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The request was not sent: a new client may try again whatever the tool does.
            Evict(call.Server.Id, pooled);
            var failure = Classify(ex, call.Server.Id);
            return (failure with { Attempts = attempt }, canRetry && failure.Failure != McpUpstreamFailure.Rejected);
        }

        var context = new CallContext(call.Method, call.ForwardedHeaders, (long)call.Server.MaxResultBytes * 2 + EnvelopeBytes);
        var parameters = (JsonObject)call.Params.DeepClone();
        string? progressToken = null;
        if (call.Progress is { } progress)
        {
            // A token of the gateway's own: callers on the shared client must not see, or collide with, each other's tokens.
            progressToken = "ume-" + Guid.NewGuid().ToString("N");
            var meta = parameters["_meta"] as JsonObject ?? [];
            meta["progressToken"] = progressToken;
            parameters["_meta"] = meta;
            pooled.Progress[progressToken] = progress;
        }

        Current.Value = context;
        try
        {
            var response = await client.SendRequestAsync(new JsonRpcRequest { Method = call.Method, Params = parameters }, deadline.Token);
            var result = response.Result;
            if (context.TooLarge || (result is not null && JsonSerializer.SerializeToUtf8Bytes(result).Length > call.Server.MaxResultBytes))
            {
                return (new McpUpstreamResult(null, McpUpstreamFailure.TooLarge, attempt), false);
            }

            return (new McpUpstreamResult(result, McpUpstreamFailure.None, attempt), false);
        }
        catch (McpProtocolException ex) when (!context.TooLarge)
        {
            return (new McpUpstreamResult(null, McpUpstreamFailure.JsonRpcError, attempt, ErrorCode: (int)ex.ErrorCode, ErrorMessage: ServerMessage(ex.Message)), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (context.TooLarge)
            {
                return (new McpUpstreamResult(null, McpUpstreamFailure.TooLarge, attempt), false);
            }

            // A broken connection or a lost session needs a new client; a slow or failing tool does not, and other
            // callers' requests on the shared client must not be cut off because of it.
            var failure = Classify(ex, call.Server.Id) with { Attempts = attempt };
            if (failure.Failure == McpUpstreamFailure.Transport || failure.HttpStatus == 404)
            {
                Evict(call.Server.Id, pooled);
            }

            // A handshake-era server answers 404 when it dropped the session: the request never ran.
            var sessionGone = failure.HttpStatus == 404 && client.SessionId is not null;
            var retry = canRetry && (sessionGone
                || (call.MayRetry && failure.CountsAgainstCircuit)
                || (failure.Failure == McpUpstreamFailure.Unauthorized && call.Auth is McpUpstreamAuth.ClientCredentials));
            return (failure, retry);
        }
        finally
        {
            Current.Value = null;
            if (progressToken is not null)
            {
                pooled.Progress.TryRemove(progressToken, out _);
            }
        }
    }

    /// <summary>The SDK puts this before the message of a JSON-RPC error the server sent; the client gets the server's own text.</summary>
    private const string RemoteErrorPrefix = "Request failed (remote): ";

    private static string ServerMessage(string message) =>
        message.StartsWith(RemoteErrorPrefix, StringComparison.Ordinal) ? message[RemoteErrorPrefix.Length..] : message;

    private McpUpstreamResult Classify(Exception ex, Guid serverId)
    {
        var status = FindStatus(ex);
        if (ex is OperationCanceledException or TimeoutException)
        {
            return new McpUpstreamResult(null, McpUpstreamFailure.Timeout, 0);
        }

        if (status is { } code)
        {
            if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                tokens.Invalidate(serverId);
                return new McpUpstreamResult(null, McpUpstreamFailure.Unauthorized, 0, (int)code);
            }

            return new McpUpstreamResult(null, (int)code >= 500 ? McpUpstreamFailure.ServerError : McpUpstreamFailure.Rejected, 0, (int)code);
        }

        return new McpUpstreamResult(null, ex is FormatException ? McpUpstreamFailure.Rejected : McpUpstreamFailure.Transport, 0);
    }

    private static HttpStatusCode? FindStatus(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is HttpRequestException { StatusCode: { } status })
            {
                return status;
            }
        }

        return null;
    }

    private PooledClient Pool(McpServer server, McpUpstreamAuth auth, Uri url, TimeSpan timeout)
    {
        while (true)
        {
            if (_clients.TryGetValue(server.Id, out var existing))
            {
                if (existing.Url == url && existing.Fingerprint == auth.Fingerprint)
                {
                    return existing;
                }

                if (_clients.TryRemove(new KeyValuePair<Guid, PooledClient>(server.Id, existing)))
                {
                    _ = existing.DisposeAsync().AsTask();
                }

                continue;
            }

            var progress = new ConcurrentDictionary<string, Func<JsonObject, ValueTask>>(StringComparer.Ordinal);
            var created = new PooledClient(url, auth.Fingerprint, CreateClientAsync(server.Id, server.Slug, url, auth, timeout, progress), progress);
            if (_clients.TryAdd(server.Id, created))
            {
                return created;
            }

            _ = created.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// Runs with no caller context, and the SDK's own requests (discover, initialize) carry only the server's credential.
    /// Not tied to one caller's cancellation: other callers may be waiting for the same client.
    /// </summary>
    private async Task<McpClient> CreateClientAsync(Guid serverId, string name, Uri url, McpUpstreamAuth auth, TimeSpan timeout,
        ConcurrentDictionary<string, Func<JsonObject, ValueTask>> progress)
    {
        await Task.Yield();
        Current.Value = null;
        var http = new HttpClient(new HeaderHandler(serverId, auth, tokens) { InnerHandler = handlers.CreateHandler(HttpClientName) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(ProviderTransport.UserAgent);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = url,
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = name,
                ConnectionTimeout = timeout,
                EnableStandaloneGetStream = false,
            },
            http, NullLoggerFactory.Instance, ownsHttpClient: true);
        using var deadline = new CancellationTokenSource(timeout);
        // The SDK's loggers can include message content, which must never be logged.
        return await McpClient.CreateAsync(transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "ume-llm-gateway", Version = ProviderTransport.UserAgent.Product!.Version ?? "0" },
                Handlers = new McpClientHandlers
                {
                    NotificationHandlers = [new(NotificationMethods.ProgressNotification, (notification, _) => RelayProgressAsync(notification, progress))],
                },
            },
            NullLoggerFactory.Instance, deadline.Token);
    }

    /// <summary>Hands a progress notification to the call that asked for it; a token no call holds any more is dropped.</summary>
    private static ValueTask RelayProgressAsync(JsonRpcNotification notification, ConcurrentDictionary<string, Func<JsonObject, ValueTask>> progress) =>
        notification.Params is JsonObject parameters && parameters["progressToken"] is JsonValue token && token.TryGetValue<string>(out var value)
            && progress.TryGetValue(value, out var relay)
            ? relay((JsonObject)parameters.DeepClone())
            : ValueTask.CompletedTask;

    private void Evict(Guid serverId, PooledClient pooled)
    {
        if (_clients.TryRemove(new KeyValuePair<Guid, PooledClient>(serverId, pooled)))
        {
            _ = pooled.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pooled in _clients.Values)
        {
            await pooled.DisposeAsync();
        }

        _clients.Clear();
    }

    private sealed class PooledClient(Uri url, string fingerprint, Task<McpClient> client, ConcurrentDictionary<string, Func<JsonObject, ValueTask>> progress)
        : IAsyncDisposable
    {
        public Uri Url { get; } = url;
        public string Fingerprint { get; } = fingerprint;
        public Task<McpClient> Client { get; } = client;

        /// <summary>Progress relays of the calls in flight, by the gateway's progress token.</summary>
        public ConcurrentDictionary<string, Func<JsonObject, ValueTask>> Progress { get; } = progress;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await (await Client).DisposeAsync();
            }
            catch (Exception)
            {
                // A client that never connected has nothing to close.
            }
        }
    }

    /// <summary>
    /// What one caller's request adds: its forwarded headers and the response size limit. Claimed by the first HTTP request
    /// that carries that JSON-RPC method, so nothing else the SDK sends during or after the call (re-initialisation,
    /// background requests that captured the context) ever carries the caller's headers.
    /// </summary>
    private sealed class CallContext(string method, IReadOnlyList<KeyValuePair<string, string>> headers, long maxResponseBytes)
    {
        private int _claimed;

        public string Method { get; } = method;
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;
        public long MaxResponseBytes { get; } = maxResponseBytes;
        public volatile bool TooLarge;

        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    /// <summary>Adds the server's credential to every request, and the caller's context to the caller's own request.</summary>
    private sealed class HeaderHandler(Guid serverId, McpUpstreamAuth auth, McpTokenCache tokens) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            switch (auth)
            {
                case McpUpstreamAuth.StaticHeaders statics:
                    foreach (var (name, value) in statics.Headers)
                    {
                        request.Headers.Remove(name);
                        request.Headers.TryAddWithoutValidation(name, value);
                    }

                    break;
                case McpUpstreamAuth.ClientCredentials credentials:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(serverId, credentials, cancellationToken));
                    break;
            }

            var context = Current.Value;
            var mine = context is not null && request.Method == HttpMethod.Post && request.Content is not null
                && await CarriesMethodAsync(request.Content, context.Method, cancellationToken) && context.TryClaim();
            if (mine)
            {
                foreach (var (name, value) in context!.Headers)
                {
                    if (IsForwardable(name) && !request.Headers.Contains(name))
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }
                }
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (mine)
            {
                response.Content = new LimitedContent(response.Content, context!);
            }

            return response;
        }

        private static async Task<bool> CarriesMethodAsync(HttpContent content, string method, CancellationToken cancellationToken)
        {
            await content.LoadIntoBufferAsync(cancellationToken);
            var body = await content.ReadAsByteArrayAsync(cancellationToken);
            try
            {
                var reader = new Utf8JsonReader(body);
                using var document = JsonDocument.ParseValue(ref reader);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("method", out var m) && m.ValueEquals(method)
                    && document.RootElement.TryGetProperty("id", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>A response body that stops (and marks the call) once it is larger than the call allows.</summary>
    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CallContext _context;

        public LimitedContent(HttpContent inner, CallContext context)
        {
            _inner = inner;
            _context = context;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() => new LimitedStream(await _inner.ReadAsStreamAsync(), _context);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var limited = new LimitedStream(await _inner.ReadAsStreamAsync(), _context);
            await limited.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class LimitedStream(Stream inner, CallContext context) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (_read > context.MaxResponseBytes)
            {
                context.TooLarge = true;
                throw new IOException("The MCP server's answer is larger than allowed.");
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
