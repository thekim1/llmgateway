using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Requests;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// Serves a Chat Completions request through the Responses API for models that cannot combine reasoning with
/// function tools on <c>/v1/chat/completions</c> (gpt-5.6, GPT-6: "Function tools with reasoning_effort are not
/// supported … use /v1/responses"). The client keeps speaking Chat Completions; the request is translated to a
/// Responses request and the answer (or stream) back, the way LiteLLM and Bifrost bridge these models.
/// Content the Responses API cannot take (audio, n &gt; 1) throws <see cref="NotSupportedException"/>.
/// </summary>
public static partial class ResponsesBridge
{
    /// <summary>gpt-5.6 and later, GPT-6 and later (also behind a prefix such as <c>azure/</c> or a date suffix).</summary>
    [GeneratedRegex(@"(^|/)gpt-(5\.[6-9]|[6-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ToolsNeedResponsesModel();

    /// <summary>Whether the model is known to need the bridge for tool calls with reasoning.</summary>
    public static bool IsKnownModel(ModelDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return deployment.ParameterProfile == ParameterProfile.OpenAIReasoning && ToolsNeedResponsesModel().IsMatch(deployment.UpstreamModel);
    }

    /// <summary>
    /// A request the bridge is for: function tools while the model reasons. <c>reasoning_effort: "none"</c> stays on
    /// Chat Completions, where these models accept tools without reasoning.
    /// </summary>
    public static bool Wants(JsonObject chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return HasTools(chat) && !(chat["reasoning_effort"] is JsonValue effort && effort.TryGetValue<string>(out var value) && value == "none");
    }

    public static bool HasTools(JsonObject chat) => chat["tools"] is JsonArray { Count: > 0 };

    /// <summary>The provider's answer when a model needs the bridge, for deployments whose name does not tell.</summary>
    public static bool IsHint(ProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.StatusCode == 400 && failure.Body is { } body
            && body.Contains("/v1/responses", StringComparison.Ordinal) && body.Contains("reasoning_effort", StringComparison.Ordinal);
    }

    public static JsonObject ToResponsesRequest(JsonObject chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (chat["n"] is JsonValue n && n.TryGetValue<int>(out var count) && count > 1)
        {
            throw new NotSupportedException("This model answers tool calls through the Responses API, which returns one choice: n must be 1.");
        }

        if (chat["audio"] is not null || (chat["modalities"] as JsonArray)?.Any(m => m?.GetValue<string>() == "audio") == true)
        {
            throw new NotSupportedException("This model answers tool calls through the Responses API, which cannot return audio.");
        }

        var result = new JsonObject
        {
            ["model"] = chat["model"]?.DeepClone(),
            ["input"] = Input(chat["messages"] as JsonArray),
            // Chat Completions stores nothing unless asked; the Responses API stores by default.
            ["store"] = chat["store"]?.DeepClone() ?? false,
        };

        if (RequestRewriter.IsStreaming(chat))
        {
            result["stream"] = true;
        }

        if ((chat["max_completion_tokens"] ?? chat["max_tokens"]) is { } max)
        {
            result["max_output_tokens"] = max.DeepClone();
        }

        if (chat["reasoning_effort"] is { } effort)
        {
            result["reasoning"] = new JsonObject { ["effort"] = effort.DeepClone() };
        }

        foreach (var name in (string[])["temperature", "top_p", "parallel_tool_calls", "metadata", "user", "safety_identifier",
                     "prompt_cache_key", "prompt_cache_retention", "service_tier"])
        {
            if (chat[name] is { } value)
            {
                result[name] = value.DeepClone();
            }
        }

        var text = new JsonObject();
        if (chat["response_format"] is JsonObject format)
        {
            text["format"] = format["type"]?.GetValue<string>() == "json_schema" && format["json_schema"] is JsonObject schema
                ? Merge(new JsonObject { ["type"] = "json_schema" }, schema)
                : format.DeepClone();
        }

        if (chat["verbosity"] is { } verbosity)
        {
            text["verbosity"] = verbosity.DeepClone();
        }

        if (text.Count > 0)
        {
            result["text"] = text;
        }

        if (chat["tools"] is JsonArray tools)
        {
            result["tools"] = new JsonArray([.. tools.OfType<JsonObject>().Select(Tool)]);
        }

        if (chat["tool_choice"] is { } choice && ToolChoice(choice) is { } mapped)
        {
            result["tool_choice"] = mapped;
        }

        return result;
    }

    public static JsonObject ToChatCompletion(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = new StringBuilder();
        string? refusal = null;
        var toolCalls = new JsonArray();
        foreach (var item in (response["output"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (item["type"]?.GetValue<string>())
            {
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        switch (part["type"]?.GetValue<string>())
                        {
                            case "output_text":
                                text.Append(part["text"]?.GetValue<string>());
                                break;
                            case "refusal":
                                refusal += part["refusal"]?.GetValue<string>();
                                break;
                        }
                    }

                    break;
                case "function_call":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = item["call_id"]?.DeepClone(),
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = item["name"]?.DeepClone(), ["arguments"] = item["arguments"]?.DeepClone() ?? "{}" },
                    });
                    break;
                case "custom_tool_call":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = item["call_id"]?.DeepClone(),
                        ["type"] = "custom",
                        ["custom"] = new JsonObject { ["name"] = item["name"]?.DeepClone(), ["input"] = item["input"]?.DeepClone() ?? string.Empty },
                    });
                    break;
            }
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null, ["refusal"] = refusal };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        var result = new JsonObject
        {
            ["id"] = response["id"]?.DeepClone(),
            ["object"] = "chat.completion",
            ["created"] = response["created_at"]?.DeepClone(),
            ["model"] = response["model"]?.DeepClone(),
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = FinishReason(response, toolCalls.Count > 0),
            }),
        };

        if (response["service_tier"] is { } tier)
        {
            result["service_tier"] = tier.DeepClone();
        }

        if (ChatUsage(response["usage"]) is { } usage)
        {
            result["usage"] = usage;
        }

        return result;
    }

    internal static string FinishReason(JsonNode? response, bool toolCalls) =>
        response?["status"]?.GetValue<string>() == "incomplete"
            ? response["incomplete_details"]?["reason"]?.GetValue<string>() == "content_filter" ? "content_filter" : "length"
            : toolCalls ? "tool_calls" : "stop";

    /// <summary>Responses usage in Chat Completions form, reasoning tokens included.</summary>
    internal static JsonObject? ChatUsage(JsonNode? usage)
    {
        if (!UsageParser.TryRead(usage, out var tokens))
        {
            return null;
        }

        var result = AnthropicTranslator.ToOpenAIUsage(tokens);
        result["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = UsageParser.Long(usage?["output_tokens_details"]?["reasoning_tokens"]) };
        return result;
    }

    /// <summary>A Responses API error (in a stream or <c>response.failed</c>) as an OpenAI error body.</summary>
    internal static string ToChatError(JsonNode? error) => new JsonObject
    {
        ["error"] = new JsonObject
        {
            ["message"] = error?["message"]?.GetValue<string>() ?? "Upstream provider error",
            ["type"] = error?["type"]?.GetValue<string>() ?? "upstream_error",
            ["code"] = error?["code"]?.DeepClone(),
        },
    }.ToJsonString();

    private static JsonArray Input(JsonArray? messages)
    {
        var input = new JsonArray();
        var customCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in (messages ?? []).OfType<JsonObject>())
        {
            switch (m["role"]?.GetValue<string>())
            {
                case "system" or "developer":
                    input.Add(new JsonObject { ["role"] = m["role"]!.DeepClone(), ["content"] = TextOf(m["content"]) });
                    break;
                case "user":
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = UserContent(m["content"]) });
                    break;
                case "assistant":
                    var text = TextOf(m["content"]);
                    if (!string.IsNullOrEmpty(text))
                    {
                        input.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
                    }

                    // No item ids: the calls are replayed without the reasoning items Chat Completions never returned.
                    foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (call["custom"] is JsonObject custom)
                        {
                            customCalls.Add(call["id"]?.GetValue<string>() ?? string.Empty);
                            input.Add(new JsonObject
                            {
                                ["type"] = "custom_tool_call", ["call_id"] = call["id"]?.DeepClone(),
                                ["name"] = custom["name"]?.DeepClone(), ["input"] = custom["input"]?.DeepClone() ?? string.Empty,
                            });
                        }
                        else
                        {
                            input.Add(new JsonObject
                            {
                                ["type"] = "function_call", ["call_id"] = call["id"]?.DeepClone(),
                                ["name"] = call["function"]?["name"]?.DeepClone(), ["arguments"] = call["function"]?["arguments"]?.DeepClone() ?? "{}",
                            });
                        }
                    }

                    break;
                case "tool":
                    var callId = m["tool_call_id"]?.GetValue<string>();
                    input.Add(new JsonObject
                    {
                        ["type"] = callId is not null && customCalls.Contains(callId) ? "custom_tool_call_output" : "function_call_output",
                        ["call_id"] = callId,
                        ["output"] = TextOf(m["content"]),
                    });
                    break;
            }
        }

        return input;
    }

    private static JsonNode UserContent(JsonNode? content)
    {
        if (content is not JsonArray parts)
        {
            return TextOf(content);
        }

        var result = new JsonArray();
        foreach (var part in parts.OfType<JsonObject>())
        {
            switch (part["type"]?.GetValue<string>())
            {
                case "text":
                    result.Add(new JsonObject { ["type"] = "input_text", ["text"] = part["text"]?.DeepClone() });
                    break;
                case "image_url":
                    var image = new JsonObject { ["type"] = "input_image", ["image_url"] = (part["image_url"]?["url"] ?? part["image_url"])?.DeepClone() };
                    image["detail"] = part["image_url"]?["detail"]?.DeepClone() ?? "auto";
                    result.Add(image);
                    break;
                case "file" when part["file"] is JsonObject file:
                    result.Add(Merge(new JsonObject { ["type"] = "input_file" }, file));
                    break;
                default:
                    throw new NotSupportedException($"This model answers tool calls through the Responses API, which cannot take '{part["type"]?.GetValue<string>()}' content.");
            }
        }

        return result;
    }

    private static JsonObject Tool(JsonObject tool) => tool["type"]?.GetValue<string>() switch
    {
        "function" when tool["function"] is JsonObject fn => Merge(new JsonObject
        {
            ["type"] = "function",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            // Chat Completions defaults to non-strict schemas; the Responses API to strict ones, which most schemas fail.
            ["strict"] = false,
        }, fn),
        "custom" when tool["custom"] is JsonObject custom => Merge(new JsonObject { ["type"] = "custom" }, custom),
        var type => throw new NotSupportedException($"This model answers tool calls through the Responses API; tools of type '{type}' cannot be translated."),
    };

    private static JsonNode? ToolChoice(JsonNode choice) => choice switch
    {
        JsonValue => choice.DeepClone(), // "auto", "none", "required"
        JsonObject { } o when o["function"]?["name"] is { } name => new JsonObject { ["type"] = "function", ["name"] = name.DeepClone() },
        JsonObject { } o when o["custom"]?["name"] is { } name => new JsonObject { ["type"] = "custom", ["name"] = name.DeepClone() },
        JsonObject { } o when o["allowed_tools"] is JsonObject allowed => new JsonObject
        {
            ["type"] = "allowed_tools",
            ["mode"] = allowed["mode"]?.DeepClone() ?? "auto",
            ["tools"] = new JsonArray([.. (allowed["tools"] as JsonArray ?? []).OfType<JsonObject>().Select(t =>
                t["function"]?["name"] is { } fn ? new JsonObject { ["type"] = "function", ["name"] = fn.DeepClone() } : t.DeepClone())]),
        },
        _ => null,
    };

    private static JsonObject Merge(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source)
        {
            target[name] = value?.DeepClone();
        }

        return target;
    }

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Concat(parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>())),
        _ => string.Empty,
    };
}

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
