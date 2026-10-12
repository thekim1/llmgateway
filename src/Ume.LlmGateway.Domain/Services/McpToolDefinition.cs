using System.Text.Json;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// An approved MCP tool definition (<see cref="Entities.McpTool.ApprovedDefinition"/>), parsed once per catalogue
/// snapshot: the JSON clients see, the input schema calls are checked against, and the behaviour hints.
/// </summary>
public sealed class McpToolDefinition
{
    private McpToolDefinition(JsonElement json)
    {
        Json = json;
        InputSchema = json.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object ? schema : null;
        var annotations = json.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        ReadOnly = Hint(annotations, "readOnlyHint") == true;
        Destructive = Hint(annotations, "destructiveHint");
        Idempotent = Hint(annotations, "idempotentHint") == true;
    }

    /// <summary>The definition object: description, inputSchema, outputSchema, annotations.</summary>
    public JsonElement Json { get; }

    public JsonElement? InputSchema { get; }
    public bool ReadOnly { get; }

    /// <summary>The <c>destructiveHint</c> annotation; null when the tool does not say.</summary>
    public bool? Destructive { get; }

    public bool Idempotent { get; }

    /// <summary>
    /// Whether a call may be sent again after a transport failure, when it may already have run. MCP's defaults apply:
    /// a tool that is not read-only counts as destructive unless it says <c>destructiveHint: false</c>, and a destructive
    /// tool is only retried when it says <c>idempotentHint: true</c>.
    /// </summary>
    public bool MayRetry => ReadOnly || Destructive == false || Idempotent;

    /// <summary>The parsed definition, or null when <paramref name="json"/> is not a JSON object.</summary>
    public static McpToolDefinition? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? new McpToolDefinition(document.RootElement.Clone()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? Hint(JsonElement annotations, string name) =>
        annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
}
