using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

public sealed record PiiScanResult(IReadOnlyDictionary<PiiCategory, int> Counts)
{
    public bool HasPii => Counts.Count > 0;

    public string? Summary => HasPii
        ? string.Join(',', Counts.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"))
        : null;
}

/// <summary>What the gateway must do with a request after PII scanning.</summary>
public enum PiiDecision
{
    Forward = 0,
    ForwardRedacted = 1,
    Block = 2,
    ForwardOnPremOnly = 3,
}

/// <summary>
/// Scans all user-provided string values inside an OpenAI/Anthropic JSON request body and optionally
/// redacts them in place. Structural fields (model, role, ids, base64 data, URLs) are skipped.
/// </summary>
public static class PiiJsonScanner
{
    private static readonly HashSet<string> SkippedProperties = new(StringComparer.Ordinal)
    {
        "model", "role", "type", "id", "tool_call_id", "call_id", "name", "url", "image_url", "data",
        "encoding_format", "response_format", "tool_choice", "stop", "user", "service_tier",
        "reasoning_effort", "previous_response_id", "media_type", "format", "detail", "file_id",
        // Base64 file payloads: matching them costs CPU per megabyte, and redacting a chance match would corrupt the file.
        "file_data", "file_url", "video_url",
        // Realtime events: base64 audio in input_audio parts, and event ids.
        "audio", "event_id", "previous_item_id", "item_id",
    };

    public static PiiScanResult Scan(JsonNode? body, bool redact)
    {
        var counts = new Dictionary<PiiCategory, int>();
        Walk(body, propertyName: null, counts, redact);
        return new PiiScanResult(counts);
    }

    /// <summary>
    /// Scans every string value in <paramref name="node"/> (MCP tool arguments and results, which have no structural
    /// fields of their own), except under the property names in <paramref name="skipped"/>.
    /// </summary>
    public static PiiScanResult ScanValues(JsonNode? node, bool redact, IReadOnlySet<string>? skipped = null)
    {
        var counts = new Dictionary<PiiCategory, int>();
        WalkValues(node, counts, redact, skipped);
        return new PiiScanResult(counts);
    }

    /// <summary>Adds <paramref name="other"/>'s counts to <paramref name="result"/>'s.</summary>
    public static PiiScanResult Merge(PiiScanResult result, PiiScanResult other)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(other);
        if (!other.HasPii)
        {
            return result;
        }

        var counts = new Dictionary<PiiCategory, int>(result.Counts);
        foreach (var (category, count) in other.Counts)
        {
            counts[category] = counts.GetValueOrDefault(category) + count;
        }

        return new PiiScanResult(counts);
    }

    public static PiiDecision Decide(PiiPolicy policy, PiiScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasPii)
        {
            return PiiDecision.Forward;
        }

        return policy switch
        {
            PiiPolicy.Redact => PiiDecision.ForwardRedacted,
            PiiPolicy.Block => PiiDecision.Block,
            PiiPolicy.RerouteToOnPrem => PiiDecision.ForwardOnPremOnly,
            _ => PiiDecision.Forward,
        };
    }

    private static void Walk(JsonNode? node, string? propertyName, Dictionary<PiiCategory, int> counts, bool redact)
    {
        switch (node)
        {
            case JsonObject obj:
                // Redaction replaces values while walking, so it needs a snapshot of the properties; detection does not.
                foreach (var (key, child) in redact ? obj.ToList() : (IEnumerable<KeyValuePair<string, JsonNode?>>)obj)
                {
                    if (SkippedProperties.Contains(key) && child is not JsonObject and not JsonArray)
                    {
                        continue;
                    }

                    if (key is "image_url" or "data" or "source" && child is JsonObject)
                    {
                        continue; // image payloads
                    }

                    Walk(child, key, counts, redact);
                }

                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    Walk(arr[i], propertyName, counts, redact);
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                Detect(value, text, counts, redact);
                break;
        }
    }

    private static void WalkValues(JsonNode? node, Dictionary<PiiCategory, int> counts, bool redact, IReadOnlySet<string>? skipped)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in redact ? obj.ToList() : (IEnumerable<KeyValuePair<string, JsonNode?>>)obj)
                {
                    if (skipped is null || !skipped.Contains(key))
                    {
                        WalkValues(child, counts, redact, skipped);
                    }
                }

                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    WalkValues(arr[i], counts, redact, skipped);
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                Detect(value, text, counts, redact);
                break;
        }
    }

    private static void Detect(JsonValue value, string text, Dictionary<PiiCategory, int> counts, bool redact)
    {
        var matches = PiiDetector.Detect(text);
        if (matches.Count == 0)
        {
            return;
        }

        foreach (var m in matches)
        {
            counts[m.Category] = counts.GetValueOrDefault(m.Category) + 1;
        }

        if (redact)
        {
            value.ReplaceWith(JsonValue.Create(PiiDetector.Redact(text, matches)));
        }
    }
}
