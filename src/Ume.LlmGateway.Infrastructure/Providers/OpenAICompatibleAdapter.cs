using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;

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
        : this(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false,
        }, requireHttps)
    {
    }

    public ProviderHttpClient(HttpMessageHandler handler, bool requireHttps = false)
    {
        RequireHttps = requireHttps;
        Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ume-LlmGateway", "0.1"));
    }

    public HttpClient Client { get; }
    internal bool RequireHttps { get; }

    public void Dispose() => Client.Dispose();
}

public abstract class ProviderAdapterBase(ProviderHttpClient http) : IProviderAdapter
{
    protected static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public abstract bool CanHandle(ProviderType type);

    public abstract Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken);

    protected static string PathFor(GatewayEndpoint endpoint) => endpoint switch
    {
        GatewayEndpoint.ChatCompletions => "chat/completions",
        GatewayEndpoint.Embeddings => "embeddings",
        GatewayEndpoint.Responses => "responses",
        GatewayEndpoint.AnthropicMessages => "messages",
        GatewayEndpoint.AudioTranscriptions => "audio/transcriptions",
        GatewayEndpoint.AudioTranslations => "audio/translations",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

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

    protected virtual void ApplyHeaders(HttpRequestMessage request, ProviderCall call)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrEmpty(call.Credential))
        {
            return;
        }

        switch (call.Provider.AuthMode)
        {
            case ProviderAuthMode.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.Credential);
                break;
            case ProviderAuthMode.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation("api-key", call.Credential);
                break;
            case ProviderAuthMode.XApiKeyHeader:
                request.Headers.TryAddWithoutValidation("x-api-key", call.Credential);
                break;
        }
    }

    /// <summary>
    /// Sends the request and returns either the (successful) response with headers read, or a failure.
    /// Caller cancellation propagates as <see cref="OperationCanceledException"/>; provider timeouts become a
    /// retryable 504 failure.
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
        if (http.RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, new ProviderFailure(503, true, "https_required"));
        }

        if (call.Stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        ApplyHeaders(request, call);

        HttpResponseMessage response;
        try
        {
            response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, new ProviderFailure(504, true, "timeout"));
        }
        catch (HttpRequestException ex)
        {
            return (null, new ProviderFailure(502, true, $"connection_error:{ex.HttpRequestError}"));
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
            return (null, new ProviderFailure(status, ProviderFailure.IsRetryableStatus(status), $"http_{status}",
                errorBody, response.Content.Headers.ContentType?.ToString()));
        }
    }

    /// <summary>
    /// Reads a complete JSON answer and its token usage without building a document tree: the bytes are passed to
    /// the client as received. Returns a failure for timeouts and invalid JSON.
    /// </summary>
    protected static async Task<ProviderResult> ReadJsonResultAsync(HttpResponseMessage response, CancellationToken timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        byte[] buffer;
        int length;
        try
        {
            (buffer, length) = await ProviderJson.ReadBodyAsync(response.Content, timeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderFailure(504, true, "timeout");
        }

        try
        {
            var usage = ProviderJson.ReadUsage(buffer.AsSpan(0, length));
            return new ProviderJsonResult((int)response.StatusCode, buffer.AsMemory(0, length), usage, buffer);
        }
        catch (JsonException)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            return new ProviderFailure(502, true, "invalid_json");
        }
    }

    protected static CancellationTokenSource CreateTimeout(ProviderCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(call.Provider.TimeoutSeconds, 1, 900)));
        return cts;
    }

    protected static async IAsyncEnumerable<SseEvent> StreamEvents(
        HttpResponseMessage response, Func<SseEvent, IEnumerable<SseEvent>> transform, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(transform);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var evt in SseReader.ReadAsync(stream, cancellationToken))
        {
            foreach (var output in transform(evt))
            {
                yield return output;
            }
        }
    }

    protected static string Serialize(JsonNode node) => node.ToJsonString(Compact);
}

/// <summary>
/// OpenAI-compatible providers: OpenAI, Azure OpenAI (v1 API), Azure AI Foundry, Ollama (/v1), Ollama Cloud,
/// vLLM and other compatible servers. Bodies pass through unchanged apart from the rewrites already applied.
/// </summary>
public sealed class OpenAICompatibleAdapter(ProviderHttpClient http) : ProviderAdapterBase(http)
{
    public override bool CanHandle(ProviderType type) => type is not ProviderType.Anthropic;

    public override async Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Endpoint == GatewayEndpoint.AnthropicMessages)
        {
            return new ProviderFailure(400, true, "endpoint_not_supported");
        }

        if (call.Audio is not null)
        {
            return await SendAudioAsync(call, cancellationToken);
        }

        var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(BuildUri(call.Provider.BaseUrl, PathFor(call.Endpoint)), call.Body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            timeout.Dispose();
            return failure;
        }

        if (!call.Stream)
        {
            using (timeout)
            using (response)
            {
                return await ReadJsonResultAsync(response!, timeout.Token, cancellationToken);
            }
        }

        // Streaming: header timeout no longer applies; the stream lives as long as the client is connected.
        timeout.Dispose();
        var acc = new UsageAccumulator();
        var events = StreamEvents(response!, evt => TransformChunk(evt, acc, call.ClientRequestedStreamUsage), cancellationToken);
        return new ProviderStreamResult(events, acc, response);
    }

    /// <summary>
    /// Speech to text: the form fields and the file go upstream as <c>multipart/form-data</c>. The answer is JSON,
    /// plain text, SRT or WebVTT depending on <c>response_format</c>, or an SSE stream (gpt-4o-transcribe with
    /// <c>stream=true</c>; Whisper ignores <c>stream</c> and answers in one piece, which is passed on as such).
    /// </summary>
    private async Task<ProviderResult> SendAudioAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        var audio = call.Audio!;
        var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(BuildUri(call.Provider.BaseUrl, PathFor(call.Endpoint)), AudioForm(call.Body, audio), call, timeout, cancellationToken);
        if (failure is not null)
        {
            timeout.Dispose();
            return failure;
        }

        var contentType = response!.Content.Headers.ContentType;
        if (call.Stream && contentType?.MediaType == "text/event-stream")
        {
            timeout.Dispose();
            var acc = new UsageAccumulator();
            return new ProviderStreamResult(StreamEvents(response, evt => TransformChunk(evt, acc, clientWantsUsage: true), cancellationToken), acc, response);
        }

        using (timeout)
        using (response)
        {
            byte[] buffer;
            int length;
            try
            {
                (buffer, length) = await ProviderJson.ReadBodyAsync(response.Content, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProviderFailure(504, true, "timeout");
            }

            var json = contentType?.MediaType is null or "application/json" || contentType.MediaType.EndsWith("+json", StringComparison.Ordinal);
            TokenUsage usage = default;
            var reported = false;
            if (json)
            {
                try
                {
                    (usage, reported) = ProviderJson.ReadTranscriptionUsage(buffer.AsSpan(0, length));
                }
                catch (JsonException)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    return new ProviderFailure(502, true, "invalid_json");
                }
            }

            // What the provider did not report is estimated: audio length from the file, transcript from the answer.
            var estimate = CostCalculator.EstimateTranscription(audio.EstimatedSeconds);
            usage = reported
                ? usage with { AudioSeconds = usage.AudioSeconds > 0 ? usage.AudioSeconds : estimate.AudioSeconds }
                : estimate with { OutputTokens = CostCalculator.EstimateTokens(length) };
            return new ProviderJsonResult((int)response.StatusCode, buffer.AsMemory(0, length), usage, buffer, json ? null : contentType!.ToString());
        }
    }

    private static MultipartFormDataContent AudioForm(JsonObject fields, AudioUpload audio)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, node) in fields)
        {
            // Repeated fields (timestamp_granularities[], include[]) were collected into an array.
            var values = node is JsonArray array ? (IEnumerable<JsonNode?>)array : [node];
            foreach (var value in values)
            {
                if (value is JsonValue v)
                {
                    var part = new StringContent(v.TryGetValue<string>(out var s) ? s : v.TryGetValue<bool>(out var b) ? (b ? "true" : "false") : v.ToJsonString());
                    part.Headers.ContentType = null; // plain form field, as clients send it
                    form.Add(part, name);
                }
            }
        }

        var file = new ReadOnlyMemoryContent(audio.Data);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(audio.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", audio.FileName);
        return form;
    }

    internal static IEnumerable<SseEvent> TransformChunk(SseEvent evt, UsageAccumulator acc, bool clientWantsUsage)
    {
        if (evt.Data == SseEvent.Done)
        {
            yield return evt;
            yield break;
        }

        // Fast path for the content chunks that make up nearly the whole stream: only the text length is needed.
        if (!evt.Data.Contains("\"usage\"", StringComparison.Ordinal))
        {
            acc.OutputCharacters += ProviderJson.OpenAIContentLength(evt.Data);
            yield return evt;
            yield break;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(evt.Data);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node is JsonObject obj)
        {
            // Chat completions chunk
            if (obj["usage"] is JsonObject && UsageParser.TryRead(obj["usage"], out var usage))
            {
                UsageParser.Apply(acc, usage);
                if (!clientWantsUsage && obj["choices"] is JsonArray { Count: 0 })
                {
                    // Usage-only chunk injected by the gateway; the client did not ask for it.
                    yield break;
                }
            }

            // Responses API: usage arrives in response.completed
            if (obj["response"]?["usage"] is JsonObject responseUsage && UsageParser.TryRead(responseUsage, out var ru))
            {
                UsageParser.Apply(acc, ru);
            }

            if (obj["choices"] is JsonArray choices)
            {
                foreach (var choice in choices)
                {
                    if (choice?["delta"]?["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                    {
                        acc.OutputCharacters += text.Length;
                    }
                }
            }
            else if (obj["delta"] is JsonValue delta && delta.TryGetValue<string>(out var responseDelta))
            {
                acc.OutputCharacters += responseDelta.Length;
            }
        }

        yield return evt;
    }
}
