using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Pii;

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
        // "user" is not here: it is free text the provider receives. "name" is skipped except on messages (see Walk).
        "encoding_format", "response_format", "tool_choice", "stop", "service_tier",
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
    /// The request's text (the same values the PII scan reads: messages, instructions, documents, never structural fields
    /// or base64 payloads), one value per line, keeping the last <paramref name="maxCharacters"/> characters: the most
    /// recent turns. For the content guard.
    /// </summary>
    public static string Text(JsonNode? body, int maxCharacters)
    {
        var parts = new List<string>();
        CollectText(body, parts);
        var text = string.Join('\n', parts);
        return text.Length <= maxCharacters ? text : text[^maxCharacters..];
    }

    private static void CollectText(JsonNode? node, List<string> parts)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (SkippedProperties.Contains(key) && child is not JsonObject and not JsonArray && !(key == "name" && obj.ContainsKey("role")))
                    {
                        continue;
                    }

                    if (key == "source" && child is JsonObject source)
                    {
                        CollectText(source["type"] is JsonValue t && t.TryGetValue<string>(out var kind) && kind is "text" or "content"
                            ? source[kind == "text" ? "data" : "content"] : null, parts);
                        continue;
                    }

                    if (key is "image_url" or "data" && child is JsonObject)
                    {
                        continue;
                    }

                    CollectText(child, parts);
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    CollectText(item, parts);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text):
                parts.Add(text);
                break;
        }
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

    /// <summary>
    /// An Anthropic content-block <c>source</c>. Images and PDFs (<c>base64</c>, <c>url</c>, <c>file</c>) are files the guard
    /// cannot read; a plain-text document (<c>type: text</c>, the text in <c>data</c>) or a custom-content document
    /// (<c>type: content</c>) is text and is scanned like any message.
    /// </summary>
    private static void WalkSource(JsonObject source, Dictionary<PiiCategory, int> counts, bool redact)
    {
        switch ((source["type"] as JsonValue)?.TryGetValue<string>(out var type) == true ? type : null)
        {
            case "text":
                Walk(source["data"], "text", counts, redact);
                break;
            case "content":
                Walk(source["content"], "content", counts, redact);
                break;
        }
    }

    private static void Walk(JsonNode? node, string? propertyName, Dictionary<PiiCategory, int> counts, bool redact)
    {
        switch (node)
        {
            case JsonObject obj:
                // Redaction replaces values while walking, so it needs a snapshot of the properties; detection does not.
                foreach (var (key, child) in redact ? obj.ToList() : (IEnumerable<KeyValuePair<string, JsonNode?>>)obj)
                {
                    // A message's "name" (the participant) is free text; a tool's or function's name is not.
                    if (SkippedProperties.Contains(key) && child is not JsonObject and not JsonArray && !(key == "name" && obj.ContainsKey("role")))
                    {
                        continue;
                    }

                    if (key == "source" && child is JsonObject source)
                    {
                        WalkSource(source, counts, redact);
                        continue;
                    }

                    if (key is "image_url" or "data" && child is JsonObject)
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

            case JsonValue value when value.TryGetValue<string>(out var original):
                // Look-alike digits and invisible characters are folded first; a redacted value is written in that form.
                var text = PiiDetector.Canonical(original);
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

                break;
        }
    }
}
