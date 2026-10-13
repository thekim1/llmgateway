using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// One server of Anthropic's MCP connector on <c>/v1/messages</c> (<c>mcp_servers</c> plus its <c>mcp_toolset</c>), which the
/// gateway runs itself against a registered MCP server (<c>name</c> = the server's slug). Only what narrows is honoured:
/// <c>default_config</c>/<c>configs</c> <c>enabled</c>, a pinned <c>tools</c> list and the deprecated
/// <c>tool_configuration</c>. The server's address and credentials are the gateway's, never the caller's.
/// </summary>
public sealed class AnthropicMcpServer
{
    private AnthropicMcpServer(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>The <c>url</c> the caller sent; it must be the registered URL.</summary>
    public string? Url { get; private init; }

    /// <summary>The toolset's <c>cache_control</c>, put on the last tool the server offers.</summary>
    public JsonNode? CacheControl { get; private init; }

    private bool EnabledByDefault { get; init; } = true;
    private Dictionary<string, bool> Configs { get; init; } = new(StringComparer.Ordinal);
    private HashSet<string>? Pinned { get; init; }
    private HashSet<string>? Allowed { get; init; }

    /// <summary>Whether the request enables <paramref name="toolName"/> (the server's name of the tool).</summary>
    public bool Enables(string toolName) =>
        (Pinned is null || Pinned.Contains(toolName)) && (Allowed is null || Allowed.Contains(toolName))
        && (Configs.TryGetValue(toolName, out var enabled) ? enabled : EnabledByDefault);

    /// <summary>Whether a <c>/v1/messages</c> request uses the connector: <c>mcp_servers</c>, or an <c>mcp_toolset</c> tool.</summary>
    public static bool IsMcpRequest(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.ContainsKey("mcp_servers"))
        {
            return true;
        }

        if (body["tools"] is JsonArray tools)
        {
            foreach (var tool in tools)
            {
                if (tool is JsonObject t && StringOf(t["type"]) == "mcp_toolset")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads <c>mcp_servers</c> and the <c>mcp_toolset</c> tools, and takes both out of <paramref name="body"/>. Null with
    /// <paramref name="error"/> (a message for the caller) when they cannot be used.
    /// </summary>
    public static IReadOnlyList<AnthropicMcpServer>? Parse(JsonObject body, out string? error)
    {
        ArgumentNullException.ThrowIfNull(body);
        error = null;
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var order = new List<string>();
        switch (body["mcp_servers"])
        {
            case null:
                break;
            case JsonArray servers:
                foreach (var node in servers)
                {
                    if (node is not JsonObject server || StringOf(server["name"]) is not { Length: > 0 } name)
                    {
                        error = "Varje post i 'mcp_servers' måste vara ett objekt med 'name' (MCP-serverns kortnamn).";
                        return null;
                    }

                    if (server["type"] is { } type && StringOf(type) != "url")
                    {
                        error = $"MCP-servern '{name}': 'type' måste vara \"url\".";
                        return null;
                    }

                    if (server.ContainsKey("authorization_token"))
                    {
                        error = $"MCP-servern '{name}': 'authorization_token' stöds inte; gatewayen använder MCP-serverns egna inloggningsuppgifter.";
                        return null;
                    }

                    if (server["url"] is { } url && StringOf(url) is null)
                    {
                        error = $"MCP-servern '{name}': 'url' måste vara en sträng.";
                        return null;
                    }

                    if (!definitions.TryAdd(name, server))
                    {
                        error = $"Två poster i 'mcp_servers' har samma 'name' ('{name}').";
                        return null;
                    }

                    order.Add(name);
                }

                break;
            default:
                error = "'mcp_servers' måste vara en lista.";
                return null;
        }

        var toolsets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (body["tools"] is JsonArray tools)
        {
            for (var i = tools.Count - 1; i >= 0; i--)
            {
                if (tools[i] is not JsonObject tool || StringOf(tool["type"]) != "mcp_toolset")
                {
                    continue;
                }

                if (StringOf(tool["mcp_server_name"]) is not { Length: > 0 } name || !definitions.ContainsKey(name))
                {
                    error = "Ett 'mcp_toolset' måste ha 'mcp_server_name' med namnet på en server i 'mcp_servers'.";
                    return null;
                }

                if (!toolsets.TryAdd(name, tool))
                {
                    error = $"MCP-servern '{name}' har mer än ett 'mcp_toolset'.";
                    return null;
                }

                tools.RemoveAt(i);
            }
        }

        var parsed = new List<AnthropicMcpServer>(order.Count);
        foreach (var name in order)
        {
            var server = definitions[name];
            var toolset = toolsets.GetValueOrDefault(name);
            if (Read(name, server, toolset, out error) is not { } read)
            {
                return null;
            }

            parsed.Add(read);
        }

        body.Remove("mcp_servers");
        return parsed;
    }

    private static AnthropicMcpServer? Read(string name, JsonObject server, JsonObject? toolset, out string? error)
    {
        error = null;
        HashSet<string>? allowed = null;
        var enabledByDefault = true;
        if (server["tool_configuration"] is { } configuration)
        {
            if (configuration is not JsonObject c || !OptionalBool(c["enabled"], out var enabled)
                || (c["allowed_tools"] is { } list && (list is not JsonArray names || Names(names) is null)))
            {
                error = $"MCP-servern '{name}': 'tool_configuration' måste vara {{\"enabled\": bool, \"allowed_tools\": [...]}}.";
                return null;
            }

            enabledByDefault = enabled ?? true;
            allowed = c["allowed_tools"] is JsonArray allowedNames ? Names(allowedNames) : null;
        }

        var configs = new Dictionary<string, bool>(StringComparer.Ordinal);
        HashSet<string>? pinned = null;
        if (toolset is not null)
        {
            if (!ToolConfig(toolset["default_config"], out var byDefault))
            {
                error = $"mcp_toolset '{name}': 'default_config' måste vara ett objekt där 'enabled' är bool.";
                return null;
            }

            enabledByDefault &= byDefault ?? true;
            switch (toolset["configs"])
            {
                case null:
                    break;
                case JsonObject perTool:
                    foreach (var (tool, config) in perTool)
                    {
                        if (!ToolConfig(config, out var enabled))
                        {
                            error = $"mcp_toolset '{name}': 'configs.{tool}' måste vara ett objekt där 'enabled' är bool.";
                            return null;
                        }

                        if (enabled is { } value)
                        {
                            configs[tool] = value;
                        }
                    }

                    break;
                default:
                    error = $"mcp_toolset '{name}': 'configs' måste vara ett objekt med verktygsnamn som nycklar.";
                    return null;
            }

            if (toolset["tools"] is { } list)
            {
                pinned = list is JsonArray entries ? PinnedNames(entries) : null;
                if (pinned is null)
                {
                    error = $"mcp_toolset '{name}': 'tools' måste vara en lista med verktyg som har 'name'.";
                    return null;
                }
            }
        }

        return new AnthropicMcpServer(name)
        {
            Url = StringOf(server["url"]),
            CacheControl = toolset?["cache_control"]?.DeepClone(),
            EnabledByDefault = enabledByDefault,
            Configs = configs,
            Pinned = pinned,
            Allowed = allowed,
        };
    }

    private static bool ToolConfig(JsonNode? node, out bool? enabled)
    {
        enabled = null;
        return node switch
        {
            null => true,
            JsonObject config => OptionalBool(config["enabled"], out enabled),
            _ => false,
        };
    }

    private static bool OptionalBool(JsonNode? node, out bool? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue v && v.TryGetValue<bool>(out var b))
        {
            value = b;
            return true;
        }

        return false;
    }

    private static HashSet<string>? PinnedNames(JsonArray entries)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject tool || StringOf(tool["name"]) is not { } name)
            {
                return null;
            }

            names.Add(name);
        }

        return names;
    }

    private static HashSet<string>? Names(JsonArray array)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (StringOf(item) is not { } name)
            {
                return null;
            }

            names.Add(name);
        }

        return names;
    }

    private static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
