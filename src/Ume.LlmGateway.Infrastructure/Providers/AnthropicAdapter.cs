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
    public const string ApiVersion = "2023-06-01";

    public override bool CanHandle(ProviderType type) => type == ProviderType.Anthropic;

    protected override void ApplyHeaders(HttpRequestMessage request, ProviderCall call)
    {
        ArgumentNullException.ThrowIfNull(request);
        base.ApplyHeaders(request, call);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }

    public override async Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var translate = call.Endpoint == GatewayEndpoint.ChatCompletions;
        if (!translate && call.Endpoint != GatewayEndpoint.AnthropicMessages)
        {
            return new ProviderFailure(400, true, "endpoint_not_supported");
        }

        var body = translate ? AnthropicTranslator.ToMessagesRequest(call.Body, call.Deployment.UpstreamModel) : call.Body;
        var timeout = CreateTimeout(call, cancellationToken);
        var (response, failure) = await SendRawAsync(BuildUri(call.Provider.BaseUrl, "messages"), body, call, timeout, cancellationToken);
        if (failure is not null)
        {
            timeout.Dispose();
            return translate && failure.Body is not null
                ? new ProviderFailure(failure.StatusCode, failure.Retryable, failure.Reason, AnthropicTranslator.ToOpenAIError(failure.Body), "application/json")
                : failure;
        }

        var created = time.GetUtcNow().ToUnixTimeSeconds();
        if (!call.Stream)
        {
            using (timeout)
            using (response)
            {
                JsonNode? json;
                try
                {
                    json = await ReadJsonAsync(response!, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ProviderFailure(504, true, "timeout");
                }
                catch (JsonException)
                {
                    return new ProviderFailure(502, true, "invalid_json");
                }

                UsageParser.TryRead(json?["usage"], out var usage);
                var output = translate && json is JsonObject msg ? AnthropicTranslator.ToChatCompletion(msg, created) : json ?? new JsonObject();
                return new ProviderJsonResult((int)response!.StatusCode, output, usage);
            }
        }

        timeout.Dispose();
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

    internal static IEnumerable<SseEvent> Passthrough(SseEvent evt, UsageAccumulator acc)
    {
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
