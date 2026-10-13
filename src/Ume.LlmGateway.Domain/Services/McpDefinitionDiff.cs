using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

public enum McpDefinitionChangeKind
{
    Added = 0,
    Removed = 1,
    Changed = 2,
}

/// <summary>One difference between two tool definitions. <c>Path</c> is a JSON Pointer (RFC 6901) into the definition.</summary>
public sealed record McpDefinitionChange(string Path, McpDefinitionChangeKind Kind, JsonNode? Before, JsonNode? After);

/// <summary>
/// What changed between an approved MCP tool definition and the one the server offers now, for the approval view. Objects
/// are compared member by member and arrays item by item; every other difference is reported at the value that differs.
/// </summary>
public static class McpDefinitionDiff
{
    /// <summary>The differences, in document order; empty when the definitions are equal. Not JSON = the whole value changed.</summary>
    public static IReadOnlyList<McpDefinitionChange> Compare(string? before, string? after)
    {
        var changes = new List<McpDefinitionChange>();
        Compare(Parse(before), Parse(after), "", changes);
        return changes;
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    private static void Compare(JsonNode? before, JsonNode? after, string path, List<McpDefinitionChange> changes)
    {
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        switch (before, after)
        {
            case (JsonObject left, JsonObject right):
                foreach (var (name, value) in left)
                {
                    if (right.TryGetPropertyValue(name, out var other))
                    {
                        Compare(value, other, path + "/" + Escape(name), changes);
                    }
                    else
                    {
                        changes.Add(new McpDefinitionChange(path + "/" + Escape(name), McpDefinitionChangeKind.Removed, value?.DeepClone(), null));
                    }
                }

                foreach (var (name, value) in right)
                {
                    if (!left.ContainsKey(name))
                    {
                        changes.Add(new McpDefinitionChange(path + "/" + Escape(name), McpDefinitionChangeKind.Added, null, value?.DeepClone()));
                    }
                }

                break;
            case (JsonArray left, JsonArray right):
                for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
                {
                    var itemPath = path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (i >= right.Count)
                    {
                        changes.Add(new McpDefinitionChange(itemPath, McpDefinitionChangeKind.Removed, left[i]?.DeepClone(), null));
                    }
                    else if (i >= left.Count)
                    {
                        changes.Add(new McpDefinitionChange(itemPath, McpDefinitionChangeKind.Added, null, right[i]?.DeepClone()));
                    }
                    else
                    {
                        Compare(left[i], right[i], itemPath, changes);
                    }
                }

                break;
            default:
                changes.Add(new McpDefinitionChange(path, McpDefinitionChangeKind.Changed, before?.DeepClone(), after?.DeepClone()));
                break;
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
