using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// A hosted <c>{"type":"mcp"}</c> tool of a <c>/v1/responses</c> request, which the gateway runs itself against a
/// registered MCP server (<c>server_label</c> = the server's slug). Only what narrows is honoured; the server's address and
/// credentials are the gateway's, never the caller's.
/// </summary>
public sealed class ResponsesMcpTool
{
    /// <summary>Response ids the gateway issues for a loop that stopped with calls still to answer.</summary>
    public const string GatewayResponsePrefix = "resp_ume_";

    private static readonly string[] RefusedMembers = ["headers", "authorization", "connector_id"];

    private ResponsesMcpTool(string serverLabel)
    {
        ServerLabel = serverLabel;
    }

    public string ServerLabel { get; }

    /// <summary>The <c>server_url</c> the caller sent; it must be the registered URL.</summary>
    public string? ServerUrl { get; private init; }

    /// <summary>Server tool names from <c>allowed_tools</c>; null means every tool the key may use.</summary>
    public IReadOnlySet<string>? AllowedTools { get; private init; }

    /// <summary><c>allowed_tools.read_only</c>: only tools annotated <c>readOnlyHint: true</c>.</summary>
    public bool ReadOnlyOnly { get; private init; }

    private bool ApprovalByDefault { get; init; } = true;
    private IReadOnlySet<string> AlwaysApprove { get; init; } = EmptySet;
    private IReadOnlySet<string> NeverApprove { get; init; } = EmptySet;

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether the request asks for approval of <paramref name="toolName"/> (the server's name). Unnamed tools need it unless the request says <c>"never"</c>.</summary>
    public bool RequiresApproval(string toolName) =>
        AlwaysApprove.Contains(toolName) || (!NeverApprove.Contains(toolName) && ApprovalByDefault);

    /// <summary>Whether the request is one the gateway runs MCP for: an <c>mcp</c> tool, or a continuation of a gateway response.</summary>
    public static bool IsMcpRequest(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body["tools"] is JsonArray tools)
        {
            foreach (var tool in tools)
            {
                if (tool is JsonObject t && t["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "mcp")
                {
                    return true;
                }
            }
        }

        return body["previous_response_id"] is JsonValue previous && previous.TryGetValue<string>(out var id)
            && id.StartsWith(GatewayResponsePrefix, StringComparison.Ordinal);
    }

    /// <summary>Reads an <c>mcp</c> tool; null with <paramref name="error"/> (a message for the caller) when it cannot be used.</summary>
    public static ResponsesMcpTool? Parse(JsonObject tool, out string? error)
    {
        ArgumentNullException.ThrowIfNull(tool);
        error = null;
        if (StringOf(tool["server_label"]) is not { Length: > 0 } label)
        {
            error = "Ett mcp-verktyg saknar 'server_label' (MCP-serverns kortnamn).";
            return null;
        }

        if (RefusedMembers.FirstOrDefault(tool.ContainsKey) is { } refused)
        {
            error = $"mcp-verktyget '{label}': '{refused}' stöds inte; gatewayen använder MCP-serverns egna inloggningsuppgifter.";
            return null;
        }

        if (tool["server_url"] is { } url && StringOf(url) is null)
        {
            error = $"mcp-verktyget '{label}': 'server_url' måste vara en sträng.";
            return null;
        }

        HashSet<string>? allowed = null;
        var readOnly = false;
        switch (tool["allowed_tools"])
        {
            case null:
                break;
            case JsonArray names when Names(names) is { } list:
                allowed = list;
                break;
            case JsonObject filter when (filter["tool_names"] is null || (filter["tool_names"] is JsonArray n && Names(n) is not null))
                && (filter["read_only"] is null || filter["read_only"] is JsonValue r && r.TryGetValue<bool>(out _)):
                allowed = filter["tool_names"] is JsonArray toolNames ? Names(toolNames) : null;
                readOnly = filter["read_only"] is JsonValue ro && ro.GetValue<bool>();
                break;
            default:
                error = $"mcp-verktyget '{label}': 'allowed_tools' måste vara en lista med verktygsnamn eller {{\"tool_names\": [...], \"read_only\": bool}}.";
                return null;
        }

        var byDefault = true;
        IReadOnlySet<string> always = EmptySet, never = EmptySet;
        switch (tool["require_approval"])
        {
            case null:
                break;
            case JsonValue value when StringOf(value) is "always" or "never":
                byDefault = StringOf(value) == "always";
                break;
            case JsonObject policy when ApprovalNames(policy["always"], out var a) && ApprovalNames(policy["never"], out var b)
                && policy.All(p => p.Key is "always" or "never"):
                always = a;
                never = b;
                break;
            default:
                error = $"mcp-verktyget '{label}': 'require_approval' måste vara \"always\", \"never\" eller {{\"always\"|\"never\": {{\"tool_names\": [...]}}}}.";
                return null;
        }

        return new ResponsesMcpTool(label)
        {
            ServerUrl = StringOf(tool["server_url"]),
            AllowedTools = allowed,
            ReadOnlyOnly = readOnly,
            ApprovalByDefault = byDefault,
            AlwaysApprove = always,
            NeverApprove = never,
        };
    }

    private static bool ApprovalNames(JsonNode? node, out IReadOnlySet<string> names)
    {
        names = EmptySet;
        if (node is null)
        {
            return true;
        }

        if (node is JsonObject filter && filter["tool_names"] is JsonArray list && Names(list) is { } parsed)
        {
            names = parsed;
            return true;
        }

        return false;
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
