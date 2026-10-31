using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Infrastructure.Providers.Anthropic;

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
                        ["usage"] = AnthropicTranslator.ToOpenAIUsage(new Domain.Usage.TokenUsage(_inputTokens, _cachedTokens, _outputTokens)),
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
