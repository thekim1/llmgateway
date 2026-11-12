using System.Text.Json.Nodes;

namespace Ume.LlmGateway.TestKit;

/// <summary>
/// The published integration contract (<c>docs/openapi/integrations.json</c>), so tests can hold the services' answers
/// to it: a field the document does not describe, or a required one that is missing, is a difference.
/// </summary>
public static class IntegrationContract
{
    private static readonly Lazy<JsonNode> Document = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UmeLlmGateway.slnx")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), "docs", "openapi", "integrations.json");
        return JsonNode.Parse(File.ReadAllText(path))!;
    });

    /// <summary>The differences between <paramref name="value"/> and the schema <paramref name="schema"/> of the contract; empty when it conforms.</summary>
    public static IReadOnlyList<string> Differences(JsonNode? value, string schema)
    {
        var differences = new List<string>();
        Check(value, Document.Value["components"]!["schemas"]![schema] ?? throw new ArgumentException($"No schema '{schema}'.", nameof(schema)), schema, differences);
        return differences;
    }

    private static void Check(JsonNode? value, JsonNode schema, string path, List<string> differences)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            var name = reference[(reference.LastIndexOf('/') + 1)..];
            Check(value, Document.Value["components"]!["schemas"]![name]!, path, differences);
            return;
        }

        if (schema["oneOf"] is JsonArray alternatives)
        {
            // Only "a schema or null" is used: a value is checked against the schema.
            if (value is not null && alternatives.FirstOrDefault(a => a?["$ref"] is not null) is { } described)
            {
                Check(value, described, path, differences);
            }

            return;
        }

        switch (value)
        {
            case JsonObject obj when schema["properties"] is JsonObject properties:
                foreach (var (name, property) in obj)
                {
                    if (properties[name] is { } described)
                    {
                        Check(property, described, $"{path}.{name}", differences);
                    }
                    else if (schema["additionalProperties"]?.GetValue<bool>() != true)
                    {
                        differences.Add($"{path}.{name} is not in the contract");
                    }
                }

                foreach (var required in schema["required"]?.AsArray() ?? [])
                {
                    if (!obj.ContainsKey(required!.GetValue<string>()))
                    {
                        differences.Add($"{path}.{required} is missing");
                    }
                }

                break;
            case JsonArray array when schema["items"] is { } items:
                for (var i = 0; i < array.Count; i++)
                {
                    Check(array[i], items, $"{path}[{i}]", differences);
                }

                break;
            case JsonValue text when schema["enum"] is JsonArray values && text.TryGetValue<string>(out var s):
                if (!values.Any(v => v!.GetValue<string>() == s))
                {
                    differences.Add($"{path} is '{s}', not one of the contract's values");
                }

                break;
            case null when schema["type"] is { } type && !(type is JsonArray types && types.Any(t => t!.GetValue<string>() == "null")):
                differences.Add($"{path} is null");
                break;
        }
    }
}
