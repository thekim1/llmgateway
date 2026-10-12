using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>The LLM API whose tool shapes <c>/v1/mcp/tools</c> and <c>/v1/mcp/tools/call</c> speak.</summary>
public enum LlmToolFormat
{
    /// <summary>Chat Completions: <c>tools[].function</c>, <c>tool_calls[]</c>, <c>role: "tool"</c> messages.</summary>
    OpenAi = 0,

    /// <summary>Responses: function tools, <c>function_call</c> and <c>function_call_output</c> items.</summary>
    Responses = 1,

    /// <summary>Anthropic Messages: tools with <c>input_schema</c>, <c>tool_use</c> and <c>tool_result</c> blocks.</summary>
    Anthropic = 2,
}

/// <summary>One tool call a model made, as the client got it from an LLM API.</summary>
/// <param name="Format">The API it came from, and the shape its result is answered in.</param>
/// <param name="CallId">The call's id, echoed in the result.</param>
/// <param name="Name">The function name: the tool's exposed name.</param>
/// <param name="Arguments">The parsed arguments; a non-object (or a string that is not JSON) is refused as invalid arguments.</param>
public sealed record LlmToolCall(LlmToolFormat Format, string CallId, string Name, JsonNode? Arguments);

/// <summary>
/// MCP tools in the tool shapes of the LLM APIs: definitions to send in <c>tools</c>, the tool calls a model makes, and
/// MCP results as the message or block that answers such a call. Text stays text; images become native image parts where
/// the format takes them in tool results (Responses, Anthropic) and a short placeholder otherwise, never base64 as text.
/// </summary>
public static class McpToolFormats
{
    public const string ErrorPrefix = "Error: ";

    /// <summary>Image types every format that takes images in tool results accepts.</summary>
    private static readonly FrozenSet<string> NativeImageTypes =
        FrozenSet.ToFrozenSet(["image/png", "image/jpeg", "image/gif", "image/webp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses the <c>format</c> query value: <c>openai</c> (default), <c>responses</c> or <c>anthropic</c>.</summary>
    public static bool TryParseFormat(string? value, out LlmToolFormat format)
    {
        format = LlmToolFormat.OpenAi;
        if (string.IsNullOrEmpty(value) || value.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("responses", StringComparison.OrdinalIgnoreCase))
        {
            format = LlmToolFormat.Responses;
            return true;
        }

        if (value.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            format = LlmToolFormat.Anthropic;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A tool definition for the <c>tools</c> of a request: the approved description and input schema, unchanged.
    /// Responses function tools default to <c>strict: true</c>, which most MCP schemas do not satisfy, so it is set to false.
    /// </summary>
    public static JsonObject Definition(LlmToolFormat format, string name, McpToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var json = definition.Json;
        var description = json.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()
            : json.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
            : null;
        JsonNode schema = definition.InputSchema is { } input ? JsonNode.Parse(input.GetRawText())! : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

        var tool = new JsonObject();
        switch (format)
        {
            case LlmToolFormat.Anthropic:
                tool["name"] = name;
                AddDescription(tool, description);
                tool["input_schema"] = schema;
                return tool;
            case LlmToolFormat.Responses:
                tool["type"] = "function";
                tool["name"] = name;
                AddDescription(tool, description);
                tool["parameters"] = schema;
                tool["strict"] = false;
                return tool;
            default:
                var function = new JsonObject { ["name"] = name };
                AddDescription(function, description);
                function["parameters"] = schema;
                tool["type"] = "function";
                tool["function"] = function;
                return tool;
        }
    }

    /// <summary>
    /// The one tool call in <paramref name="body"/>: a Chat Completions <c>tool_calls[]</c> entry, a Responses
    /// <c>function_call</c> item or an Anthropic <c>tool_use</c> block. Null when it is none of them or lacks its id or
    /// name. Object arguments are taken out of <paramref name="body"/>.
    /// </summary>
    public static LlmToolCall? ParseCall(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var type = StringOf(body["type"]);
        if (type == "function_call")
        {
            return StringOf(body["call_id"]) is { Length: > 0 } callId && StringOf(body["name"]) is { Length: > 0 } name
                ? new LlmToolCall(LlmToolFormat.Responses, callId, name, Arguments(body, "arguments"))
                : null;
        }

        if (type == "tool_use")
        {
            return StringOf(body["id"]) is { Length: > 0 } id && StringOf(body["name"]) is { Length: > 0 } name
                ? new LlmToolCall(LlmToolFormat.Anthropic, id, name, Arguments(body, "input"))
                : null;
        }

        if (type is null or "function" && body["function"] is JsonObject function)
        {
            return StringOf(body["id"]) is { Length: > 0 } id && StringOf(function["name"]) is { Length: > 0 } name
                ? new LlmToolCall(LlmToolFormat.OpenAi, id, name, Arguments(function, "arguments"))
                : null;
        }

        return null;
    }

    /// <summary>
    /// The answer to <paramref name="call"/> from an MCP <c>tools/call</c> result: a <c>role: "tool"</c> message, a
    /// <c>function_call_output</c> item or a <c>tool_result</c> block. <c>structuredContent</c> is sent as JSON text only
    /// when the result has no text block (servers that return both put the same data in the text, as MCP asks). A tool
    /// error (<c>isError</c>) is <c>is_error</c> for Anthropic and text starting with <see cref="ErrorPrefix"/> otherwise.
    /// </summary>
    public static JsonObject Result(LlmToolCall call, JsonNode? result)
    {
        ArgumentNullException.ThrowIfNull(call);
        var isError = IsError(result);
        var parts = Parts(result, nativeImages: call.Format != LlmToolFormat.OpenAi);
        if (isError && call.Format != LlmToolFormat.Anthropic)
        {
            if (parts.Count > 0 && parts[0].Text is { } first)
            {
                parts[0] = new Part(ErrorPrefix + first, null, null);
            }
            else
            {
                parts.Insert(0, new Part(ErrorPrefix.TrimEnd(), null, null));
            }
        }

        switch (call.Format)
        {
            case LlmToolFormat.Anthropic:
                var answer = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = call.CallId };
                if (parts.Count > 0)
                {
                    answer["content"] = new JsonArray([.. parts.Select(p => (JsonNode)(p.Text is { } text
                        ? new JsonObject { ["type"] = "text", ["text"] = text }
                        : new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = p.MimeType, ["data"] = p.Data },
                        }))]);
                }

                if (isError)
                {
                    answer["is_error"] = true;
                }

                return answer;
            case LlmToolFormat.Responses:
                JsonNode output = parts.All(p => p.Text is not null)
                    ? JoinText(parts)
                    : new JsonArray([.. parts.Select(p => (JsonNode)(p.Text is { } text
                        ? new JsonObject { ["type"] = "input_text", ["text"] = text }
                        : new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{p.MimeType};base64,{p.Data}" }))]);
                return new JsonObject { ["type"] = "function_call_output", ["call_id"] = call.CallId, ["output"] = output };
            default:
                return new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.CallId, ["content"] = JoinText(parts) };
        }
    }

    /// <summary>The result as text only (images and audio as placeholders), without the error prefix: an <c>mcp_call</c>'s <c>output</c> or <c>error</c>.</summary>
    public static string Text(JsonNode? result) => JoinText(Parts(result, nativeImages: false));

    /// <summary>Whether the tool reported its own failure (<c>isError: true</c>).</summary>
    public static bool IsError(JsonNode? result) => result?["isError"] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag;

    /// <summary>A text, or an image the format takes natively.</summary>
    private readonly record struct Part(string? Text, string? MimeType, string? Data);

    private static List<Part> Parts(JsonNode? result, bool nativeImages)
    {
        var parts = new List<Part>();
        var hasText = false;
        foreach (var block in result?["content"] as JsonArray ?? [])
        {
            var type = StringOf(block?["type"]);
            switch (type)
            {
                case "text":
                    hasText = true;
                    if (StringOf(block!["text"]) is { Length: > 0 } text)
                    {
                        parts.Add(new Part(text, null, null));
                    }

                    break;
                case "image" or "audio":
                    var mime = StringOf(block!["mimeType"]) ?? "application/octet-stream";
                    var data = StringOf(block["data"]) ?? string.Empty;
                    parts.Add(type == "image" && nativeImages && NativeImageTypes.Contains(mime)
                        ? new Part(null, mime, data)
                        : new Part($"[{type}: {mime}, {DecodedLength(data)} bytes]", null, null));
                    break;
                case "resource_link":
                    parts.Add(new Part(LinkText(StringOf(block!["uri"]), StringOf(block["name"])), null, null));
                    break;
                case "resource":
                    var resource = block!["resource"];
                    parts.Add(StringOf(resource?["text"]) is { } contents
                        ? new Part(contents, null, null)
                        : new Part($"[resource: {StringOf(resource?["uri"])}, {StringOf(resource?["mimeType"]) ?? "application/octet-stream"}, {DecodedLength(StringOf(resource?["blob"]) ?? string.Empty)} bytes]", null, null));
                    break;
                default:
                    parts.Add(new Part($"[{type ?? "unknown"} content]", null, null));
                    break;
            }
        }

        if (!hasText && result?["structuredContent"] is { } structured)
        {
            parts.Add(new Part(structured.ToJsonString(), null, null));
        }

        return parts;
    }

    private static string LinkText(string? uri, string? name) =>
        name is { Length: > 0 } && name != uri ? $"[resource link: {uri} ({name})]" : $"[resource link: {uri}]";

    private static string JoinText(List<Part> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(text.Length > 0 ? "\n\n" : string.Empty).Append(part.Text);
        }

        return text.ToString();
    }

    /// <summary>The length of base64 <paramref name="data"/> once decoded, without decoding it.</summary>
    private static long DecodedLength(string data)
    {
        var padding = data.EndsWith("==", StringComparison.Ordinal) ? 2 : data.EndsWith('=') ? 1 : 0;
        return Math.Max(0, (data.Length / 4 * 3) - padding);
    }

    /// <summary>
    /// The call's arguments: a JSON string (Chat Completions, Responses; empty means none) is parsed, an object is taken
    /// out of <paramref name="owner"/>. A string that is not JSON is returned as it is, so it is refused as invalid.
    /// </summary>
    private static JsonNode? Arguments(JsonObject owner, string field)
    {
        var value = owner[field];
        if (value is JsonValue v && v.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException)
            {
                return JsonValue.Create(text);
            }
        }

        owner.Remove(field);
        return value;
    }

    private static void AddDescription(JsonObject tool, string? description)
    {
        if (!string.IsNullOrEmpty(description))
        {
            tool["description"] = description;
        }
    }

    private static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
