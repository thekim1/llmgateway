using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// The members of a <c>tools/list</c> entry that make up a tool's definition, which an admin approves. The name is the
    /// tool's identity; <c>_meta</c>, icons and anything else a server adds are not pinned and not passed on.
    /// </summary>
    public static IReadOnlyList<string> Members { get; } = ["title", "description", "inputSchema", "outputSchema", "annotations"];

    /// <summary>The definition of <paramref name="tool"/> (an entry of <c>tools/list</c>): its <see cref="Members"/>, unchanged.</summary>
    public static string FromTool(JsonObject tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var definition = new JsonObject();
        foreach (var member in Members)
        {
            if (tool[member] is { } value)
            {
                definition[member] = value.DeepClone();
            }
        }

        return definition.ToJsonString();
    }

    /// <summary>
    /// The <see cref="Entities.McpTool.DefinitionHash"/> of a definition: hex SHA-256 of its canonical JSON (object members
    /// sorted by name at every level, no whitespace), so the same definition has the same hash however it was serialised
    /// (Postgres <c>jsonb</c> reorders members). Throws <see cref="JsonException"/> when it is not valid JSON.
    /// </summary>
    public static string Hash(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var node = JsonNode.Parse(definition);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, node);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var (name, member) in value.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    WriteCanonical(writer, member);
                }

                writer.WriteEndObject();
                break;
            case JsonArray items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static bool? Hint(JsonElement annotations, string name) =>
        annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
}
