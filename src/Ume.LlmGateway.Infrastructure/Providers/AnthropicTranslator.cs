using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// Translates between the OpenAI Chat Completions format (what clients send to /v1/chat/completions) and the
/// Anthropic Messages API. Scope: text, images, PDF/text documents, tools/tool results, streaming and usage.
/// Content the Messages API cannot take (audio, other file types) throws <see cref="NotSupportedException"/>
/// rather than being dropped.
/// </summary>
public static class AnthropicTranslator
{
    public const int DefaultMaxTokens = 4096;

    public static JsonObject ToMessagesRequest(JsonObject chat, string upstreamModel, PromptCacheMode caching = PromptCacheMode.Off)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var result = new JsonObject { ["model"] = upstreamModel };
        var systemParts = new List<string>();
        var messages = new JsonArray();

        if (chat["messages"] is JsonArray input)
        {
            foreach (var m in input.OfType<JsonObject>())
            {
                var role = m["role"]?.GetValue<string>();
                switch (role)
                {
                    case "system" or "developer":
                        systemParts.Add(TextOf(m["content"]));
                        break;
                    case "user":
                        Append(messages, "user", ContentBlocks(m["content"]));
                        break;
                    case "assistant":
                        var blocks = new JsonArray();
                        var text = TextOf(m["content"]);
                        if (!string.IsNullOrEmpty(text))
                        {
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                        }

                        if (m["tool_calls"] is JsonArray calls)
                        {
                            foreach (var call in calls.OfType<JsonObject>())
                            {
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = call["id"]?.DeepClone(),
                                    ["name"] = call["function"]?["name"]?.DeepClone(),
                                    ["input"] = ParseArguments(call["function"]?["arguments"]),
                                });
                            }
                        }

                        Append(messages, "assistant", blocks);
                        break;
                    case "tool":
                        Append(messages, "user", new JsonArray(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = m["tool_call_id"]?.DeepClone(),
                            ["content"] = TextOf(m["content"]),
                        }));
                        break;
                }
            }
        }

        if (systemParts.Count > 0)
        {
            result["system"] = string.Join("\n\n", systemParts);
        }

        result["messages"] = messages;
        result["max_tokens"] = (chat["max_completion_tokens"] ?? chat["max_tokens"])?.DeepClone() ?? DefaultMaxTokens;

        foreach (var name in (string[])["temperature", "top_p", "top_k"])
        {
            if (chat[name] is { } value)
            {
                result[name] = value.DeepClone();
            }
        }

        if (chat["stop"] is JsonValue stopValue)
        {
            result["stop_sequences"] = new JsonArray(stopValue.DeepClone());
        }
        else if (chat["stop"] is JsonArray stopArray)
        {
            result["stop_sequences"] = stopArray.DeepClone();
        }

        if (chat["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var isStream) && isStream)
        {
            result["stream"] = true;
        }

        if (chat["tools"] is JsonArray tools)
        {
            var anthropicTools = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                if (tool["function"] is not JsonObject fn)
                {
                    continue;
                }

                var t = new JsonObject
                {
                    ["name"] = fn["name"]?.DeepClone(),
                    ["input_schema"] = fn["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                };
                if (fn["description"] is { } description)
                {
                    t["description"] = description.DeepClone();
                }

                anthropicTools.Add(t);
            }

            result["tools"] = anthropicTools;
        }

        if (chat["tool_choice"] is { } choice)
        {
            JsonNode? mapped = choice switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s switch
                {
                    "auto" => new JsonObject { ["type"] = "auto" },
                    "required" => new JsonObject { ["type"] = "any" },
                    "none" => new JsonObject { ["type"] = "none" },
                    _ => null,
                },
                JsonObject o when o["function"]?["name"] is { } name => new JsonObject { ["type"] = "tool", ["name"] = name.DeepClone() },
                _ => null,
            };
            if (mapped is not null)
            {
                result["tool_choice"] = mapped;
            }
        }

        AddCacheMarkers(result, caching);
        return result;
    }

    /// <summary>
    /// Cache breakpoints, in Anthropic's prefix order (tools, system, messages): the last tool, the system prompt and,
    /// for <see cref="PromptCacheMode.Conversation"/>, the last block of the last message. Three of the four allowed.
    /// </summary>
    private static void AddCacheMarkers(JsonObject request, PromptCacheMode caching)
    {
        if (caching == PromptCacheMode.Off)
        {
            return;
        }

        if (request["tools"] is JsonArray { Count: > 0 } tools && tools[^1] is JsonObject lastTool)
        {
            lastTool["cache_control"] = Ephemeral();
        }

        if (request["system"] is JsonValue system && system.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            request["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = Ephemeral() });
        }

        if (caching == PromptCacheMode.Conversation
            && request["messages"] is JsonArray { Count: > 0 } messages
            && messages[^1]?["content"] is JsonArray { Count: > 0 } content
            && content[^1] is JsonObject lastBlock)
        {
            lastBlock["cache_control"] = Ephemeral();
        }

        static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };
    }

    public static JsonObject ToChatCompletion(JsonObject message, long created)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = new System.Text.StringBuilder();
        var toolCalls = new JsonArray();
        if (message["content"] is JsonArray content)
        {
            foreach (var block in content.OfType<JsonObject>())
            {
                switch (block["type"]?.GetValue<string>())
                {
                    case "text":
                        text.Append(block["text"]?.GetValue<string>());
                        break;
                    case "tool_use":
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = block["id"]?.DeepClone(),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = block["name"]?.DeepClone(),
                                ["arguments"] = block["input"]?.ToJsonString() ?? "{}",
                            },
                        });
                        break;
                }
            }
        }

        var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null };
        if (toolCalls.Count > 0)
        {
            msg["tool_calls"] = toolCalls;
        }

        var result = new JsonObject
        {
            ["id"] = "chatcmpl-" + message["id"]?.GetValue<string>(),
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = message["model"]?.DeepClone(),
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = msg,
                ["finish_reason"] = MapStopReason(message["stop_reason"]?.GetValue<string>()),
            }),
        };

        if (UsageParser.TryRead(message["usage"], out var usage))
        {
            result["usage"] = ToOpenAIUsage(usage);
        }

        return result;
    }

    public static JsonObject ToOpenAIUsage(Domain.Services.TokenUsage usage) => new()
    {
        ["prompt_tokens"] = usage.InputTokens,
        ["completion_tokens"] = usage.OutputTokens,
        ["total_tokens"] = usage.InputTokens + usage.OutputTokens,
        ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = usage.CachedInputTokens },
    };

    /// <summary>Converts an Anthropic error body into an OpenAI-style error body.</summary>
    public static string ToOpenAIError(string? anthropicBody)
    {
        string message = "Upstream provider error";
        string type = "upstream_error";
        try
        {
            if (anthropicBody is not null && JsonNode.Parse(anthropicBody)?["error"] is JsonObject err)
            {
                message = err["message"]?.GetValue<string>() ?? message;
                type = err["type"]?.GetValue<string>() ?? type;
            }
        }
        catch (JsonException)
        {
        }

        return new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = null } }.ToJsonString();
    }

    public static string? MapStopReason(string? stopReason) => stopReason switch
    {
        null => null,
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "refusal" => "content_filter",
        _ => "stop",
    };

    private static void Append(JsonArray messages, string role, JsonArray blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        // Anthropic expects alternating roles; merge consecutive messages of the same role.
        if (messages.Count > 0 && messages[^1] is JsonObject last && last["role"]?.GetValue<string>() == role && last["content"] is JsonArray existing)
        {
            foreach (var b in blocks.ToList())
            {
                blocks.Remove(b);
                existing.Add(b);
            }

            return;
        }

        messages.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
    }

    private static JsonArray ContentBlocks(JsonNode? content)
    {
        var blocks = new JsonArray();
        switch (content)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = s });
                break;
            case JsonArray parts:
                foreach (var part in parts.OfType<JsonObject>())
                {
                    switch (part["type"]?.GetValue<string>())
                    {
                        case "text":
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = part["text"]?.DeepClone() });
                            break;
                        case "image_url":
                            var url = part["image_url"]?["url"]?.GetValue<string>() ?? part["image_url"]?.GetValue<string>();
                            if (url is not null)
                            {
                                blocks.Add(new JsonObject { ["type"] = "image", ["source"] = ImageSource(url) });
                            }

                            break;
                        case "file":
                            blocks.Add(DocumentBlock(part["file"] as JsonObject));
                            break;
                        case "input_audio":
                            throw new NotSupportedException("Claude models do not accept audio. Choose a model that supports audio.");
                        case "video_url" or "input_video":
                            throw new NotSupportedException("Claude models do not accept video. Choose a model that supports video.");
                        case var other:
                            // Dropping a part would silently send a different question than the client asked.
                            throw new NotSupportedException($"Content of type '{other}' cannot be sent to a Claude model.");
                    }
                }

                break;
        }

        return blocks;
    }

    /// <summary>
    /// OpenAI <c>{"type":"file","file":{"file_data":"data:…;base64,…","filename":…}}</c> → Anthropic <c>document</c>.
    /// Claude reads PDFs and plain text; other formats and OpenAI file ids cannot be forwarded.
    /// </summary>
    private static JsonObject DocumentBlock(JsonObject? file)
    {
        var filename = file?["filename"]?.GetValue<string>();
        if (file?["file_data"]?.GetValue<string>() is not { Length: > 0 } fileData)
        {
            throw new NotSupportedException(file?["file_id"] is not null
                ? "OpenAI file references (file_id) cannot be used with a Claude model. Send the file as file_data instead."
                : "The file has no content (file_data).");
        }

        string mediaType, data;
        if (fileData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = fileData.IndexOf(',', StringComparison.Ordinal);
            mediaType = fileData[5..Math.Max(5, comma)].Split(';')[0].ToLowerInvariant();
            data = comma < 0 ? string.Empty : fileData[(comma + 1)..];
        }
        else
        {
            // Some clients send bare base64 and let the file name carry the type.
            mediaType = Path.GetExtension(filename)?.ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".txt" or ".md" or ".csv" => "text/plain",
                _ => string.Empty,
            };
            data = fileData;
        }

        JsonObject source;
        if (mediaType == "application/pdf")
        {
            source = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data };
        }
        else if (mediaType.StartsWith("text/", StringComparison.Ordinal))
        {
            string text;
            try
            {
                text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data));
            }
            catch (FormatException)
            {
                throw new NotSupportedException("The file content is not valid base64.");
            }

            source = new JsonObject { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = text };
        }
        else
        {
            throw new NotSupportedException($"Claude models can read PDF and text files, not '{(mediaType.Length > 0 ? mediaType : filename ?? "unknown file type")}'.");
        }

        var block = new JsonObject { ["type"] = "document", ["source"] = source };
        if (!string.IsNullOrWhiteSpace(filename))
        {
            block["title"] = filename;
        }

        return block;
    }

    private static JsonObject ImageSource(string url)
    {
        // data:image/png;base64,....
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',', StringComparison.Ordinal);
            var meta = url[5..Math.Max(5, comma)];
            var mediaType = meta.Split(';')[0];
            return new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = url[(comma + 1)..] };
        }

        return new JsonObject { ["type"] = "url", ["url"] = url };
    }

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
            .Where(p => p["type"]?.GetValue<string>() == "text")
            .Select(p => p["text"]?.GetValue<string>())),
        _ => string.Empty,
    };

    private static JsonNode ParseArguments(JsonNode? arguments)
    {
        if (arguments is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                return JsonNode.Parse(s) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }

        return arguments is JsonObject o ? o.DeepClone() : new JsonObject();
    }
}

/// <summary>Stateful translator of Anthropic streaming events into OpenAI chat.completion.chunk events.</summary>
public sealed class AnthropicStreamTranslator(UsageAccumulator usage, bool includeUsage, long created)
{
    private const byte DeltaOther = 0, DeltaText = 1, DeltaToolJson = 2;

    private readonly Dictionary<int, int> _toolIndexByBlock = [];
    private readonly ArrayBufferWriter<byte> _buffer = new(512);
    private Utf8JsonWriter? _writer;
    private string _id = "chatcmpl-stream";
    private string? _model;
    private long _inputTokens;
    private long _cachedTokens;
    private long _cacheWriteTokens;
    private long _outputTokens;

    /// <summary>
    /// One Anthropic event in, the OpenAI chunks it becomes out. The events of a stream's body (text and tool-argument
    /// deltas, pings, block ends) take a fast path that reads and writes UTF-8 directly; the few others build JSON trees.
    /// </summary>
    public IEnumerable<SseEvent> Translate(SseEvent evt)
    {
        switch (evt.EventName)
        {
            case "ping" or "content_block_stop":
                return [];
            case "content_block_delta" when TryTranslateDelta(evt.Data, out var chunk):
                return chunk is { } c ? [c] : [];
            default:
                return TranslateEvent(evt);
        }
    }

    /// <summary>
    /// A <c>content_block_delta</c> without a JSON tree: the delta's text or partial tool arguments are copied as UTF-8
    /// into a reused writer. False when the event is not shaped as expected (the general path then handles it); a delta
    /// of another kind (thinking, citations) gives no chunk.
    /// </summary>
    private bool TryTranslateDelta(string data, out SseEvent? chunk)
    {
        chunk = null;
        byte[]? rentedInput = null;
        byte[]? rentedValue = null;
        var maxBytes = Encoding.UTF8.GetMaxByteCount(data.Length);
        Span<byte> utf8 = maxBytes <= 1024 ? stackalloc byte[1024] : (rentedInput = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var reader = new Utf8JsonReader(utf8[..Encoding.UTF8.GetBytes(data, utf8)]);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var index = -1;
            var kind = DeltaOther;
            scoped ReadOnlySpan<byte> value = default;
            var hasValue = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("index"u8))
                {
                    if (!reader.Read() || !reader.TryGetInt32(out index))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("delta"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    {
                        return false;
                    }

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("type"u8))
                        {
                            reader.Read();
                            kind = reader.ValueTextEquals("text_delta"u8) ? DeltaText
                                : reader.ValueTextEquals("input_json_delta"u8) ? DeltaToolJson
                                : DeltaOther;
                        }
                        else if (reader.ValueTextEquals("text"u8) || reader.ValueTextEquals("partial_json"u8))
                        {
                            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                            {
                                return false;
                            }

                            if (reader.ValueIsEscaped)
                            {
                                rentedValue = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);
                                value = rentedValue.AsSpan(0, reader.CopyString(rentedValue));
                            }
                            else
                            {
                                value = reader.ValueSpan;
                            }

                            hasValue = true;
                        }
                        else
                        {
                            reader.Read();
                            reader.Skip();
                        }
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (kind == DeltaText && hasValue)
            {
                usage.OutputCharacters += Encoding.UTF8.GetCharCount(value);
                var writer = BeginChunk();
                writer.WriteString("content"u8, value);
                chunk = EndChunk(writer);
            }
            else if (kind == DeltaToolJson && hasValue && _toolIndexByBlock.TryGetValue(index, out var toolIndex))
            {
                var writer = BeginChunk();
                writer.WriteStartArray("tool_calls"u8);
                writer.WriteStartObject();
                writer.WriteNumber("index"u8, toolIndex);
                writer.WriteStartObject("function"u8);
                writer.WriteString("arguments"u8, value);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
                chunk = EndChunk(writer);
            }

            return true;
        }
        catch (JsonException)
        {
            return true; // invalid JSON gives no chunk, as on the general path
        }
        finally
        {
            if (rentedInput is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedInput);
            }

            if (rentedValue is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedValue);
            }
        }
    }

    /// <summary>Starts a chunk in the reused buffer, up to the open <c>delta</c> object (same fields as <see cref="Chunk"/>).</summary>
    private Utf8JsonWriter BeginChunk()
    {
        _buffer.ResetWrittenCount();
        var writer = _writer ??= new Utf8JsonWriter(_buffer);
        writer.Reset(_buffer);
        writer.WriteStartObject();
        writer.WriteString("id"u8, _id);
        writer.WriteString("object"u8, "chat.completion.chunk"u8);
        writer.WriteNumber("created"u8, created);
        if (_model is null)
        {
            writer.WriteNull("model"u8);
        }
        else
        {
            writer.WriteString("model"u8, _model);
        }

        writer.WriteStartArray("choices"u8);
        writer.WriteStartObject();
        writer.WriteNumber("index"u8, 0);
        writer.WriteStartObject("delta"u8);
        return writer;
    }

    private SseEvent EndChunk(Utf8JsonWriter writer)
    {
        writer.WriteEndObject(); // delta
        writer.WriteNull("finish_reason"u8);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return new SseEvent(null, Encoding.UTF8.GetString(_buffer.WrittenSpan));
    }

    private IEnumerable<SseEvent> TranslateEvent(SseEvent evt)
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

        if (data is null)
        {
            yield break;
        }

        var type = data["type"]?.GetValue<string>() ?? evt.EventName;
        switch (type)
        {
            case "message_start":
                var message = data["message"];
                _id = "chatcmpl-" + message?["id"]?.GetValue<string>();
                _model = message?["model"]?.GetValue<string>();
                if (UsageParser.TryRead(message?["usage"], out var startUsage))
                {
                    _inputTokens = startUsage.InputTokens;
                    _cachedTokens = startUsage.CachedInputTokens;
                    _cacheWriteTokens = startUsage.CacheWriteInputTokens;
                    _outputTokens = startUsage.OutputTokens;
                    Update();
                }

                yield return Chunk(new JsonObject { ["role"] = "assistant", ["content"] = string.Empty }, null);
                break;

            case "content_block_start":
                if (data["content_block"] is JsonObject block && block["type"]?.GetValue<string>() == "tool_use")
                {
                    var blockIndex = (int)UsageParser.Long(data["index"]);
                    var toolIndex = _toolIndexByBlock.Count;
                    _toolIndexByBlock[blockIndex] = toolIndex;
                    yield return Chunk(new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = toolIndex,
                            ["id"] = block["id"]?.DeepClone(),
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = block["name"]?.DeepClone(), ["arguments"] = string.Empty },
                        }),
                    }, null);
                }

                break;

            case "content_block_delta":
                var delta = data["delta"];
                switch (delta?["type"]?.GetValue<string>())
                {
                    case "text_delta":
                        var text = delta["text"]?.GetValue<string>() ?? string.Empty;
                        usage.OutputCharacters += text.Length;
                        yield return Chunk(new JsonObject { ["content"] = text }, null);
                        break;
                    case "input_json_delta":
                        var idx = (int)UsageParser.Long(data["index"]);
                        if (_toolIndexByBlock.TryGetValue(idx, out var ti))
                        {
                            yield return Chunk(new JsonObject
                            {
                                ["tool_calls"] = new JsonArray(new JsonObject
                                {
                                    ["index"] = ti,
                                    ["function"] = new JsonObject { ["arguments"] = delta["partial_json"]?.DeepClone() },
                                }),
                            }, null);
                        }

                        break;
                }

                break;

            case "message_delta":
                if (data["usage"]?["output_tokens"] is { } outTokens)
                {
                    _outputTokens = UsageParser.Long(outTokens);
                    Update();
                }

                var stop = AnthropicTranslator.MapStopReason(data["delta"]?["stop_reason"]?.GetValue<string>());
                if (stop is not null)
                {
                    yield return Chunk(new JsonObject(), stop);
                }

                break;

            case "message_stop":
                if (includeUsage)
                {
                    var final = new JsonObject
                    {
                        ["id"] = _id,
                        ["object"] = "chat.completion.chunk",
                        ["created"] = created,
                        ["model"] = _model,
                        ["choices"] = new JsonArray(),
                        ["usage"] = AnthropicTranslator.ToOpenAIUsage(new Domain.Services.TokenUsage(_inputTokens, _cachedTokens, _outputTokens)),
                    };
                    yield return new SseEvent(null, final.ToJsonString());
                }

                yield return new SseEvent(null, SseEvent.Done);
                break;

            case "error":
                yield return new SseEvent(null, AnthropicTranslator.ToOpenAIError(evt.Data));
                yield return new SseEvent(null, SseEvent.Done);
                break;
        }
    }

    private void Update()
    {
        usage.InputTokens = _inputTokens;
        usage.CachedInputTokens = _cachedTokens;
        usage.CacheWriteInputTokens = _cacheWriteTokens;
        usage.OutputTokens = _outputTokens;
        usage.Reported = true;
    }

    private SseEvent Chunk(JsonObject delta, string? finishReason)
    {
        var chunk = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = _model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason,
            }),
        };
        return new SseEvent(null, chunk.ToJsonString());
    }
}
