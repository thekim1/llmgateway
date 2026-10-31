using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Usage;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// OpenAI-compatible providers: OpenAI, Azure OpenAI (v1 API), Azure AI Foundry, Ollama (/v1), Ollama Cloud,
/// vLLM and other compatible servers. Bodies pass through unchanged apart from the rewrites already applied.
/// </summary>
public sealed class OpenAICompatibleAdapter(ProviderHttpClient http) : ProviderAdapterBase(http)
{
    /// <summary>Deployments the provider told to use the Responses API for tool calls (<see cref="ResponsesBridge.IsHint"/>).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _bridged = new();

    public override bool CanHandle(ProviderType type) => type is not ProviderType.Anthropic;

    public override async Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (GatewayEndpoints.Info(call.Endpoint).IsAnthropic)
        {
            return ProviderFailure.EndpointNotSupported;
        }

        if (call.Audio is not null)
        {
            return await SendAudioAsync(call, cancellationToken);
        }

        if (GatewayEndpoints.Info(call.Endpoint).IsImage)
        {
            return await SendImageAsync(call, cancellationToken);
        }

        var chat = call.Endpoint == GatewayEndpoint.ChatCompletions;
        if (chat && ResponsesBridge.Wants(call.Body) && (ResponsesBridge.IsKnownModel(call.Deployment) || _bridged.ContainsKey(call.Deployment.Id)))
        {
            return await SendThroughResponsesAsync(call, cancellationToken);
        }

        // The timeout covers the headers and a complete answer; a stream lives as long as the client is connected.
        using var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(ProviderTransport.BuildUri(call.Provider.BaseUrl, ProviderTransport.UpstreamPath(call.Endpoint)), call.Body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            // A deployment whose name did not tell us it needs the bridge: the provider says so. Remembered until restart.
            if (chat && ResponsesBridge.HasTools(call.Body) && ResponsesBridge.IsHint(failure))
            {
                _bridged.TryAdd(call.Deployment.Id, 0);
                return await SendThroughResponsesAsync(call, cancellationToken);
            }

            return failure;
        }

        if (!call.Stream)
        {
            using (response)
            {
                return await ReadJsonResultAsync(response!, timeout.Token, cancellationToken);
            }
        }

        var acc = new UsageAccumulator();
        var events = StreamEvents(response!, evt => TransformChunk(evt, acc, call.ClientRequestedStreamUsage), cancellationToken);
        return new ProviderStreamResult(events, acc, response);
    }

    /// <summary>
    /// A Chat Completions request sent as a Responses API request (see <see cref="ResponsesBridge"/>); the answer or
    /// stream comes back in Chat Completions form, so the client never sees the difference.
    /// </summary>
    private async Task<ProviderResult> SendThroughResponsesAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        JsonObject body;
        try
        {
            body = ResponsesBridge.ToResponsesRequest(call.Body);
        }
        catch (NotSupportedException ex)
        {
            return UnsupportedContent(ex);
        }

        using var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(ProviderTransport.BuildUri(call.Provider.BaseUrl, ProviderTransport.UpstreamPath(GatewayEndpoint.Responses)), body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (call.Stream)
        {
            var acc = new UsageAccumulator();
            var translator = new ResponsesStreamTranslator(acc, call.ClientRequestedStreamUsage);
            return new ProviderStreamResult(StreamEvents(response!, translator.Translate, cancellationToken), acc, response);
        }

        using (response)
        {
            var result = await ReadJsonResultAsync(response!, timeout.Token, cancellationToken);
            if (result is not ProviderJsonResult raw)
            {
                return result;
            }

            using (raw)
            {
                return JsonNode.Parse(raw.Body.Span) is JsonObject answer
                    ? new ProviderJsonResult(raw.StatusCode, JsonSerializer.SerializeToUtf8Bytes(ResponsesBridge.ToChatCompletion(answer), Compact), raw.Usage)
                    : new ProviderJsonResult(raw.StatusCode, raw.Body.ToArray(), raw.Usage);
            }
        }
    }

    /// <summary>
    /// Speech to text: the form fields and the file go upstream as <c>multipart/form-data</c>. The answer is JSON,
    /// plain text, SRT or WebVTT depending on <c>response_format</c>, or an SSE stream (gpt-4o-transcribe with
    /// <c>stream=true</c>; Whisper ignores <c>stream</c> and answers in one piece, which is passed on as such).
    /// </summary>
    private async Task<ProviderResult> SendAudioAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(ProviderTransport.BuildUri(call.Provider.BaseUrl, ProviderTransport.UpstreamPath(call.Endpoint)), AudioForm(call.Body, call.Audio!), call, timeout, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (call.Stream && response!.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var acc = new UsageAccumulator();
            return new ProviderStreamResult(StreamEvents(response, evt => TransformChunk(evt, acc, clientWantsUsage: true), cancellationToken), acc, response);
        }

        using (response)
        {
            return await ReadCompleteAsync(response!, call.Audio!, static (response, buffer, length, audio) =>
            {
                var contentType = response.Content.Headers.ContentType;
                var json = contentType?.MediaType is null or "application/json" || contentType.MediaType.EndsWith("+json", StringComparison.Ordinal);
                var (usage, reported) = json ? ProviderJson.ReadTranscriptionUsage(buffer.AsSpan(0, length)) : default;

                // What the provider did not report is estimated: audio length from the file, transcript from the answer.
                var estimate = CostCalculator.EstimateTranscription(audio.EstimatedSeconds);
                usage = reported
                    ? usage with { AudioSeconds = usage.AudioSeconds > 0 ? usage.AudioSeconds : estimate.AudioSeconds }
                    : estimate with { OutputTokens = CostCalculator.EstimateTokens(length) };
                return new ProviderJsonResult((int)response.StatusCode, buffer.AsMemory(0, length), usage, buffer, json ? null : contentType!.ToString());
            }, timeout.Token, cancellationToken);
        }
    }

    /// <summary>
    /// Image generation (a JSON body) and editing (a multipart upload of the pictures and an optional mask). The answer is
    /// one JSON document, passed on as received: <c>data[]</c> holds base64 pictures or URLs, and token-billed models
    /// (gpt-image-1) report <c>usage</c>. Streaming (<c>partial_images</c>) is refused before the call, so it is never asked for.
    /// </summary>
    private async Task<ProviderResult> SendImageAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(call, cancellationToken);
        var uri = ProviderTransport.BuildUri(call.Provider.BaseUrl, ProviderTransport.UpstreamPath(call.Endpoint));
        var (response, failure) = call.Images is { } images
            ? await SendRawAsync(uri, ImageForm(call.Body, images), call, timeout, cancellationToken)
            : await SendRawAsync(uri, call.Body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        using (response)
        {
            return await ReadCompleteAsync(response!, 0, static (response, buffer, length, _) =>
                new ProviderJsonResult((int)response.StatusCode, buffer.AsMemory(0, length), ProviderJson.ReadImageUsage(buffer.AsSpan(0, length)), buffer),
                timeout.Token, cancellationToken);
        }
    }

    private static MultipartFormDataContent AudioForm(JsonObject fields, AudioUpload audio)
    {
        var form = FormFields(fields);
        var file = new ReadOnlyMemoryContent(audio.Data);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(audio.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", audio.FileName);
        return form;
    }

    private static MultipartFormDataContent ImageForm(JsonObject fields, ImageUploads images)
    {
        var form = FormFields(fields);
        foreach (var image in images.Files)
        {
            var file = new ReadOnlyMemoryContent(image.Data);
            file.Headers.ContentType = MediaTypeHeaderValue.TryParse(image.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, image.Field, image.FileName);
        }

        return form;
    }

    private static MultipartFormDataContent FormFields(JsonObject fields)
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
        // Real OpenAI/Azure chunks carry "usage":null, which counts as no usage.
        if (!ProviderJson.HasUsageValue(evt.Data))
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
