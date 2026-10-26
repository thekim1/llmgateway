using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// Anthropic Messages API. Serves /v1/messages as passthrough (usage captured) and /v1/chat/completions by
/// translating OpenAI ⇄ Anthropic formats.
/// </summary>
public sealed class AnthropicAdapter(ProviderHttpClient http, TimeProvider time) : ProviderAdapterBase(http)
{
    /// <summary>Sent as <c>anthropic-version</c> by <see cref="ProviderAuth"/>.</summary>
    public const string ApiVersion = "2023-06-01";

    public override bool CanHandle(ProviderType type) => type == ProviderType.Anthropic;

    public override async Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var translate = call.Endpoint == GatewayEndpoint.ChatCompletions;
        if (call.Endpoint == GatewayEndpoint.AnthropicCountTokens)
        {
            return await CountTokensAsync(call, cancellationToken);
        }

        if (!translate && call.Endpoint != GatewayEndpoint.AnthropicMessages)
        {
            return ProviderFailure.EndpointNotSupported;
        }

        JsonObject body;
        try
        {
            body = translate ? AnthropicTranslator.ToMessagesRequest(call.Body, call.Deployment.UpstreamModel, call.PromptCaching) : call.Body;
        }
        catch (NotSupportedException ex)
        {
            return UnsupportedContent(ex);
        }

        // The timeout covers the headers and a complete answer; a stream lives as long as the client is connected.
        using var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(ProviderTransport.BuildUri(call.Provider.BaseUrl, ProviderTransport.UpstreamPath(GatewayEndpoint.AnthropicMessages)), body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            return translate && failure.Body is not null
                ? new ProviderFailure(failure.StatusCode, failure.Kind, failure.Reason, AnthropicTranslator.ToOpenAIError(failure.Body), "application/json", failure.RetryAfter)
                : failure;
        }

        var created = time.GetUtcNow().ToUnixTimeSeconds();
        if (!call.Stream)
        {
            using (response)
            {
                var result = await ReadJsonResultAsync(response!, timeout.Token, cancellationToken);
                if (!translate || result is not ProviderJsonResult raw)
                {
                    return result; // /v1/messages: passed through as received
                }

                using (raw)
                {
                    if (JsonNode.Parse(raw.Body.Span) is not JsonObject msg)
                    {
                        return new ProviderJsonResult(raw.StatusCode, raw.Body.ToArray(), raw.Usage);
                    }

                    return new ProviderJsonResult(raw.StatusCode, JsonSerializer.SerializeToUtf8Bytes(AnthropicTranslator.ToChatCompletion(msg, created), Compact), raw.Usage);
                }
            }
        }

        var acc = new UsageAccumulator();
        IAsyncEnumerable<SseEvent> events;
        if (translate)
        {
            var translator = new AnthropicStreamTranslator(acc, call.ClientRequestedStreamUsage, created);
            events = StreamEvents(response!, translator.Translate, cancellationToken);
        }
        else
        {
            events = StreamEvents(response!, evt => Passthrough(evt, acc), cancellationToken);
        }

        return new ProviderStreamResult(events, acc, response);
    }

    /// <summary>
    /// <c>/v1/messages/count_tokens</c>, passed through. The answer's <c>input_tokens</c> is a count, not consumption,
    /// so the result carries no usage.
    /// </summary>
    private async Task<ProviderResult> CountTokensAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(ProviderTransport.BuildUri(call.Provider.BaseUrl, ProviderTransport.UpstreamPath(call.Endpoint)), call.Body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        using (response)
        {
            return await ReadCompleteAsync(response!, 0,
                static (response, buffer, length, _) =>
                {
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, length)); // validates; throws for invalid JSON
                    return new ProviderJsonResult((int)response.StatusCode, buffer.AsMemory(0, length), default, buffer);
                },
                timeout.Token, cancellationToken);
        }
    }

    internal static IEnumerable<SseEvent> Passthrough(SseEvent evt, UsageAccumulator acc)
    {
        // Fast paths by SSE event name (Anthropic always sends it, equal to the data's "type").
        switch (evt.EventName)
        {
            case "content_block_delta":
                acc.OutputCharacters += ProviderJson.AnthropicTextLength(evt.Data);
                return [evt];
            case "ping" or "content_block_start" or "content_block_stop" or "message_stop":
                return [evt];
        }

        try
        {
            if (JsonNode.Parse(evt.Data) is JsonObject data)
            {
                var type = data["type"]?.GetValue<string>();
                if (type == "message_start" && UsageParser.TryRead(data["message"]?["usage"], out var u))
                {
                    UsageParser.Apply(acc, u);
                }
                else if (type == "message_delta" && data["usage"]?["output_tokens"] is { } outTokens)
                {
                    acc.OutputTokens = UsageParser.Long(outTokens);
                    acc.Reported = true;
                }
                else if (type == "content_block_delta" && (data["delta"]?["text"]) is JsonValue t && t.TryGetValue<string>(out var text))
                {
                    acc.OutputCharacters += text.Length;
                }
            }
        }
        catch (JsonException)
        {
        }

        return [evt];
    }
}
