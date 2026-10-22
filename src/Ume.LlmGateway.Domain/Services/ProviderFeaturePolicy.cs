using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>Why <see cref="ProviderFeaturePolicy"/> refused a request, and the field or tool type that caused it.</summary>
public readonly record struct ProviderFeatureRefusal(bool Storage, string Feature);

/// <summary>
/// Request fields that send content further than the provider and region a key's residency rules describe. Both are off
/// for every key unless an admin allows them:
/// <list type="bullet">
/// <item><b>Provider tools</b> (<see cref="Entities.VirtualKey.AllowProviderTools"/>): tools the provider runs itself, such as
/// web search, file search, code interpreter, MCP servers and web fetch. They send the conversation to third parties or the
/// open internet. Without permission only tools the client runs (functions) are accepted.</item>
/// <item><b>Provider storage</b> (<see cref="Entities.VirtualKey.AllowProviderStorage"/>): <c>store</c>, background responses and
/// conversations keep the conversation at the provider for later retrieval. Without permission they are refused, and
/// Responses API requests get <c>store: false</c> (the API keeps responses unless told otherwise).</item>
/// </list>
/// </summary>
public static class ProviderFeaturePolicy
{
    /// <summary>Tools the client runs and answers: functions, and free-form custom tools.</summary>
    private static readonly HashSet<string> OpenAIClientTools = new(StringComparer.Ordinal) { "function", "custom" };

    private static readonly string[] OpenAIToolFields = ["web_search_options"];
    private static readonly string[] AnthropicToolFields = ["mcp_servers", "container"];

    /// <summary>
    /// Checks <paramref name="body"/> and, when storage is not allowed, sets <c>store: false</c> on a Responses API request that
    /// does not say. Returns the first refused feature, or null.
    /// </summary>
    public static ProviderFeatureRefusal? Apply(JsonObject body, GatewayEndpoint endpoint, bool allowTools, bool allowStorage)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (endpoint is not (GatewayEndpoint.ChatCompletions or GatewayEndpoint.Responses or GatewayEndpoint.AnthropicMessages))
        {
            return null;
        }

        if (!allowTools)
        {
            if (ProviderTool(body["tools"], anthropic: endpoint == GatewayEndpoint.AnthropicMessages) is { } tool)
            {
                return new ProviderFeatureRefusal(Storage: false, tool);
            }

            // Search models of Chat Completions search the web without a tool; Anthropic connects to MCP servers named here.
            foreach (var field in endpoint == GatewayEndpoint.AnthropicMessages ? AnthropicToolFields : OpenAIToolFields)
            {
                if (body[field] is { } value && !IsEmpty(value))
                {
                    return new ProviderFeatureRefusal(Storage: false, field);
                }
            }
        }

        if (allowStorage || endpoint == GatewayEndpoint.AnthropicMessages)
        {
            return null;
        }

        if (IsTrue(body["store"]))
        {
            return new ProviderFeatureRefusal(Storage: true, "store");
        }

        if (endpoint == GatewayEndpoint.Responses)
        {
            if (IsTrue(body["background"]))
            {
                return new ProviderFeatureRefusal(Storage: true, "background");
            }

            if (body["conversation"] is { } conversation && !IsEmpty(conversation))
            {
                return new ProviderFeatureRefusal(Storage: true, "conversation");
            }

            body["store"] = false;
        }

        return null;
    }

    /// <summary>The type of the first tool in <paramref name="tools"/> the provider would run itself, or null.</summary>
    public static string? ProviderTool(JsonNode? tools, bool anthropic)
    {
        if (tools is not JsonArray list)
        {
            return null;
        }

        foreach (var tool in list)
        {
            // A tool without a type is a client tool (Anthropic) or invalid (OpenAI, which the provider then refuses).
            if (tool is JsonObject t && t["type"] is JsonValue value && value.TryGetValue<string>(out var type) &&
                !(anthropic ? IsAnthropicClientTool(type) : OpenAIClientTools.Contains(type)))
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// Custom tools, and the Anthropic-defined tools the client executes (bash, text editor, computer use, memory).
    /// Server tools (web search, web fetch, code execution, tool search, MCP) run at Anthropic.
    /// </summary>
    private static bool IsAnthropicClientTool(string type) =>
        type == "custom" || type.StartsWith("bash_", StringComparison.Ordinal) || type.StartsWith("text_editor_", StringComparison.Ordinal) ||
        type.StartsWith("computer_", StringComparison.Ordinal) || type.StartsWith("memory_", StringComparison.Ordinal);

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>An empty list, <c>false</c> or <c>""</c>. An empty object is not empty: <c>"web_search_options": {}</c> turns search on.</summary>
    private static bool IsEmpty(JsonNode node) => node switch
    {
        JsonArray array => array.Count == 0,
        JsonValue value => value.TryGetValue<bool>(out var flag) ? !flag : value.TryGetValue<string>(out var text) && text.Length == 0,
        _ => false,
    };
}
