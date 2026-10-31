using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// What every provider adapter shares: sending a request (provider timeouts and connection errors become transient
/// failures), reading a complete answer without building a document tree, and reading an SSE stream.
/// </summary>
public abstract class ProviderAdapterBase(ProviderHttpClient http) : IProviderAdapter
{
    protected static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public abstract bool CanHandle(ProviderType type);

    public abstract Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the request and returns either the (successful) response with headers read, or a failure.
    /// Caller cancellation propagates as <see cref="OperationCanceledException"/>; provider timeouts become a
    /// transient 504 failure.
    /// </summary>
    protected Task<(HttpResponseMessage? Response, ProviderFailure? Failure)> SendRawAsync(
        Uri uri, JsonObject body, ProviderCall call, CancellationTokenSource timeout, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Compact));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return SendRawAsync(uri, content, call, timeout, cancellationToken);
    }

    /// <summary>As above with any request content (the multipart upload of the audio endpoints). The content is disposed.</summary>
    protected async Task<(HttpResponseMessage? Response, ProviderFailure? Failure)> SendRawAsync(
        Uri uri, HttpContent content, ProviderCall call, CancellationTokenSource timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentNullException.ThrowIfNull(call);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        if (!http.Permits(uri))
        {
            return (null, ProviderFailure.HttpsRequired);
        }

        if (call.Stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        ProviderAuth.Apply(request, call.Provider, call.Credential);

        HttpResponseMessage response;
        try
        {
            response = await (call.Stream ? http.StreamClient : http.Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, ProviderFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return (null, new ProviderFailure(502, FailureKind.Transient, $"connection_error:{ex.HttpRequestError}"));
        }

        if (response.IsSuccessStatusCode)
        {
            return (response, null);
        }

        using (response)
        {
            string? errorBody = null;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            var status = (int)response.StatusCode;
            return (null, ProviderFailure.FromStatus(status, errorBody, response.Content.Headers.ContentType?.ToString(),
                ProviderTransport.RetryAfter(response, http.Time.GetUtcNow())));
        }
    }

    /// <summary>
    /// Reads a complete JSON answer and its token usage without building a document tree: the bytes are passed to
    /// the client as received. Returns a failure for timeouts and invalid JSON.
    /// </summary>
    protected static Task<ProviderResult> ReadJsonResultAsync(HttpResponseMessage response, CancellationToken timeout, CancellationToken cancellationToken) =>
        ReadCompleteAsync(response, 0,
            static (response, buffer, length, _) => new ProviderJsonResult((int)response.StatusCode, buffer.AsMemory(0, length), ProviderJson.ReadUsage(buffer.AsSpan(0, length)), buffer),
            timeout, cancellationToken);

    /// <summary>
    /// Reads a complete answer into a pooled buffer and hands it to <paramref name="read"/>, which builds the result that
    /// owns the buffer (and reads the usage). A timeout becomes <see cref="ProviderFailure.Timeout"/>; a
    /// <see cref="JsonException"/> from <paramref name="read"/> returns the buffer and becomes <see cref="ProviderFailure.InvalidJson"/>.
    /// <paramref name="state"/> keeps <paramref name="read"/> a static lambda, so the hot path allocates no closure.
    /// </summary>
    protected static async Task<ProviderResult> ReadCompleteAsync<TState>(HttpResponseMessage response, TState state,
        Func<HttpResponseMessage, byte[], int, TState, ProviderResult> read, CancellationToken timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(read);
        byte[] buffer;
        int length;
        try
        {
            (buffer, length) = await ProviderJson.ReadBodyAsync(response.Content, timeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderFailure.Timeout;
        }

        try
        {
            return read(response, buffer, length, state);
        }
        catch (JsonException)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            return ProviderFailure.InvalidJson;
        }
    }

    protected static CancellationTokenSource CreateTimeout(ProviderCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProviderTransport.RequestTimeout(call.Provider));
        return cts;
    }

    protected static async IAsyncEnumerable<SseEvent> StreamEvents(
        HttpResponseMessage response, Func<SseEvent, IEnumerable<SseEvent>> transform, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(transform);
        await using var stream = Decoded(await response.Content.ReadAsStreamAsync(cancellationToken), response.Content.Headers.ContentEncoding);
        await foreach (var evt in SseReader.ReadAsync(stream, cancellationToken))
        {
            foreach (var output in transform(evt))
            {
                yield return output;
            }
        }
    }

    /// <summary>
    /// The stream client asks for no compression, but an upstream may compress anyway: decode it here rather than hand
    /// compressed bytes to the SSE reader.
    /// </summary>
    internal static Stream Decoded(Stream stream, ICollection<string> contentEncoding) =>
        contentEncoding.Count == 0 ? stream : contentEncoding.Last().ToLowerInvariant() switch
        {
            "gzip" => new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress),
            "br" => new System.IO.Compression.BrotliStream(stream, System.IO.Compression.CompressionMode.Decompress),
            "deflate" => new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionMode.Decompress),
            _ => stream,
        };

    protected static string Serialize(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>
    /// The client's content cannot be translated for the provider: answer 400 without touching circuit health, another
    /// provider would refuse it too.
    /// </summary>
    protected static ProviderFailure UnsupportedContent(NotSupportedException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var error = new JsonObject { ["error"] = new JsonObject { ["message"] = ex.Message, ["type"] = "invalid_request_error", ["code"] = "unsupported_content" } };
        return new ProviderFailure(400, FailureKind.ClientError, "unsupported_content", error.ToJsonString(), "application/json");
    }
}
