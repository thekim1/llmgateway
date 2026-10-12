using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// Checks MCP tool arguments against the approved <c>inputSchema</c> before they are sent upstream. A deliberate subset
/// of JSON Schema: <c>type</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties: false</c>, <c>items</c>,
/// <c>enum</c>, <c>const</c>, string length and number bounds. Other keywords (<c>$ref</c>, <c>oneOf</c>, formats, ...)
/// are not checked here; the upstream server still validates fully. The point is to stop malformed calls early, not to
/// be a complete validator.
/// </summary>
public static class McpSchemaValidator
{
    private const int MaxDepth = 32;

    /// <summary>The first problem found, as a short English message with a JSON pointer, or null when the value passes.</summary>
    public static string? Validate(JsonElement schema, JsonNode? value) => Check(schema, value, "", 0);

    private static string? Check(JsonElement schema, JsonNode? value, string path, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > MaxDepth)
        {
            return null;
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            return $"{Where(path)} must be {TypeName(type)}";
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonNode.DeepEquals(JsonNode.Parse(constant.GetRawText()), value))
        {
            return $"{Where(path)} must be {constant.GetRawText()}";
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(a => JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), value)))
        {
            return $"{Where(path)} must be one of {allowed.GetRawText()}";
        }

        return value switch
        {
            JsonObject obj => CheckObject(schema, obj, path, depth),
            JsonArray array => CheckArray(schema, array, path, depth),
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => CheckString(schema, scalar.GetValue<string>(), path),
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => CheckNumber(schema, scalar.GetValue<double>(), path),
            _ => null,
        };
    }

    private static string? CheckObject(JsonElement schema, JsonObject obj, string path, int depth)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !obj.ContainsKey(name.GetString()!))
                {
                    return $"{Where(path + "/" + name.GetString())} is required";
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var (name, child) in obj)
        {
            if (hasProperties && properties.TryGetProperty(name, out var childSchema))
            {
                if (Check(childSchema, child, path + "/" + name, depth + 1) is { } error)
                {
                    return error;
                }
            }
            else if (closed)
            {
                return $"{Where(path + "/" + name)} is not allowed";
            }
            else if (additional.ValueKind == JsonValueKind.Object && Check(additional, child, path + "/" + name, depth + 1) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static string? CheckArray(JsonElement schema, JsonArray array, string path, int depth)
    {
        if (schema.TryGetProperty("minItems", out var min) && min.TryGetInt32(out var minItems) && array.Count < minItems)
        {
            return $"{Where(path)} must have at least {minItems} items";
        }

        if (schema.TryGetProperty("maxItems", out var max) && max.TryGetInt32(out var maxItems) && array.Count > maxItems)
        {
            return $"{Where(path)} must have at most {maxItems} items";
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (Check(items, array[i], path + "/" + i.ToString(CultureInfo.InvariantCulture), depth + 1) is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? CheckString(JsonElement schema, string text, string path)
    {
        if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var minLength) && text.Length < minLength)
        {
            return $"{Where(path)} must be at least {minLength} characters";
        }

        return schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maxLength) && text.Length > maxLength
            ? $"{Where(path)} must be at most {maxLength} characters"
            : null;
    }

    private static string? CheckNumber(JsonElement schema, double number, string path)
    {
        if (schema.TryGetProperty("minimum", out var min) && min.TryGetDouble(out var minimum) && number < minimum)
        {
            return $"{Where(path)} must be at least {min.GetRawText()}";
        }

        if (schema.TryGetProperty("maximum", out var max) && max.TryGetDouble(out var maximum) && number > maximum)
        {
            return $"{Where(path)} must be at most {max.GetRawText()}";
        }

        if (schema.TryGetProperty("exclusiveMinimum", out var exMin) && exMin.TryGetDouble(out var exclusiveMinimum) && number <= exclusiveMinimum)
        {
            return $"{Where(path)} must be greater than {exMin.GetRawText()}";
        }

        return schema.TryGetProperty("exclusiveMaximum", out var exMax) && exMax.TryGetDouble(out var exclusiveMaximum) && number >= exclusiveMaximum
            ? $"{Where(path)} must be less than {exMax.GetRawText()}"
            : null;
    }

    private static bool MatchesType(JsonElement type, JsonNode? value) => type.ValueKind switch
    {
        JsonValueKind.String => IsType(type.GetString()!, value),
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && IsType(t.GetString()!, value)),
        _ => true,
    };

    private static bool IsType(string type, JsonNode? value) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && IsInteger(v),
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "null" => value is null,
        _ => true,
    };

    private static bool IsInteger(JsonValue value) => value.TryGetValue<long>(out _) || (value.TryGetValue<double>(out var d) && Math.Floor(d) == d);

    private static string TypeName(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(t => t.ToString())) : $"of type {type.GetString()}";

    private static string Where(string path) => path.Length == 0 ? "The arguments" : $"Argument '{path}'";
}
