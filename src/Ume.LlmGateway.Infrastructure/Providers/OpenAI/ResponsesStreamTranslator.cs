using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Infrastructure.Providers.OpenAI;

/// <summary>
/// Turns a Responses API event stream into Chat Completions chunks: text, refusals and tool calls as deltas, the finish
/// reason and (when the client asked for it) a usage chunk at the end, then <c>[DONE]</c>, which the Responses API does
/// not send. Reasoning summaries have no Chat Completions field and are left out.
/// </summary>
public sealed class ResponsesStreamTranslator(UsageAccumulator usage, bool includeUsage)
{
    private readonly Dictionary<string, int> _toolIndexByItem = new(StringComparer.Ordinal);
    private string? _id;
    private string? _model;
    private long _created;

    public IEnumerable<SseEvent> Translate(SseEvent evt)
    {
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(evt.Data) as JsonObject;
        }
        catch (JsonException)
        {
            yield break;
        }

        switch (data?["type"]?.GetValue<string>() ?? evt.EventName)
        {
            case "response.created":
                _id = data?["response"]?["id"]?.GetValue<string>();
                _model = data?["response"]?["model"]?.GetValue<string>();
                _created = UsageParser.Long(data?["response"]?["created_at"]);
                yield return Chunk(new JsonObject { ["role"] = "assistant", ["content"] = string.Empty }, null);
                break;

            case "response.output_text.delta":
                var text = data?["delta"]?.GetValue<string>() ?? string.Empty;
                usage.OutputCharacters += text.Length;
                yield return Chunk(new JsonObject { ["content"] = text }, null);
                break;

            case "response.refusal.delta":
                yield return Chunk(new JsonObject { ["refusal"] = data?["delta"]?.DeepClone() }, null);
                break;

            case "response.output_item.added" when (data?["item"]) is JsonObject item:
                var custom = item["type"]?.GetValue<string>() == "custom_tool_call";
                if (!custom && item["type"]?.GetValue<string>() != "function_call")
                {
                    break;
                }

                var index = _toolIndexByItem.Count;
                _toolIndexByItem[item["id"]?.GetValue<string>() ?? string.Empty] = index;
                var call = new JsonObject { ["index"] = index, ["id"] = item["call_id"]?.DeepClone(), ["type"] = custom ? "custom" : "function" };
                call[custom ? "custom" : "function"] = custom
                    ? new JsonObject { ["name"] = item["name"]?.DeepClone(), ["input"] = string.Empty }
                    : new JsonObject { ["name"] = item["name"]?.DeepClone(), ["arguments"] = string.Empty };
                yield return Chunk(new JsonObject { ["tool_calls"] = new JsonArray(call) }, null);
                break;

            case "response.function_call_arguments.delta" or "response.custom_tool_call_input.delta":
                if (_toolIndexByItem.TryGetValue(data?["item_id"]?.GetValue<string>() ?? string.Empty, out var toolIndex))
                {
                    var isFunction = data?["type"]?.GetValue<string>() == "response.function_call_arguments.delta";
                    var part = new JsonObject();
                    part[isFunction ? "arguments" : "input"] = data?["delta"]?.DeepClone();
                    var delta = new JsonObject { ["index"] = toolIndex };
                    delta[isFunction ? "function" : "custom"] = part;
                    yield return Chunk(new JsonObject { ["tool_calls"] = new JsonArray(delta) }, null);
                }

                break;

            case "response.completed" or "response.incomplete":
                var response = data?["response"];
                if (UsageParser.TryRead(response?["usage"], out var tokens))
                {
                    UsageParser.Apply(usage, tokens);
                }

                yield return Chunk(new JsonObject(), ResponsesBridge.FinishReason(response, _toolIndexByItem.Count > 0));
                if (includeUsage && ResponsesBridge.ChatUsage(response?["usage"]) is { } chatUsage)
                {
                    var final = Envelope(new JsonArray());
                    final["usage"] = chatUsage;
                    yield return new SseEvent(null, final.ToJsonString());
                }

                yield return new SseEvent(null, SseEvent.Done);
                break;

            case "response.failed":
                yield return new SseEvent(null, ResponsesBridge.ToChatError(data?["response"]?["error"]));
                yield return new SseEvent(null, SseEvent.Done);
                break;

            case "error":
                yield return new SseEvent(null, ResponsesBridge.ToChatError(data?["error"] ?? data));
                yield return new SseEvent(null, SseEvent.Done);
                break;
        }
    }

    private SseEvent Chunk(JsonObject delta, string? finishReason) =>
        new(null, Envelope(new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason })).ToJsonString());

    private JsonObject Envelope(JsonArray choices) => new()
    {
        ["id"] = _id,
        ["object"] = "chat.completion.chunk",
        ["created"] = _created,
        ["model"] = _model,
        ["choices"] = choices,
    };
}
