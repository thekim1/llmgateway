using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// The PII guard for MCP tool calls. Arguments are scanned in full (the tool name is not part of them). Results are
/// scanned in their <c>content</c> blocks (text, embedded resource text) and <c>structuredContent</c>; base64 payloads,
/// MIME types, URIs and <c>_meta</c> are skipped, since redacting a chance match there would corrupt the result.
/// </summary>
public static class McpPii
{
    private static readonly FrozenSet<string> ContentStructural =
        FrozenSet.ToFrozenSet(["type", "data", "blob", "mimeType", "uri", "_meta", "annotations"], StringComparer.Ordinal);

    private static readonly FrozenSet<string> ResourceStructural =
        FrozenSet.ToFrozenSet(["uri", "mimeType", "blob", "_meta", "annotations", "name", "title"], StringComparer.Ordinal);

    private static readonly FrozenSet<string> PromptStructural =
        FrozenSet.ToFrozenSet(["type", "data", "blob", "mimeType", "uri", "_meta", "annotations", "role"], StringComparer.Ordinal);

    public static PiiScanResult ScanArguments(JsonObject? arguments, bool redact) => PiiJsonScanner.ScanValues(arguments, redact);

    /// <summary>A <c>resources/read</c> result: the <c>text</c> of each content; blobs, URIs and MIME types are skipped.</summary>
    public static PiiScanResult ScanResourceContents(JsonNode? result, bool redact) =>
        PiiJsonScanner.ScanValues((result as JsonObject)?["contents"], redact, ResourceStructural);

    /// <summary>A <c>prompts/get</c> result: its description and the content of its messages, scanned like tool content.</summary>
    public static PiiScanResult ScanPrompt(JsonNode? result, bool redact)
    {
        if (result is not JsonObject obj)
        {
            return PiiJsonScanner.ScanValues(null, redact);
        }

        var messages = PiiJsonScanner.ScanValues(obj["messages"], redact, PromptStructural);
        return PiiJsonScanner.Merge(messages, PiiJsonScanner.ScanValues(obj["description"], redact));
    }

    public static PiiScanResult ScanResult(JsonNode? result, bool redact)
    {
        if (result is not JsonObject obj)
        {
            return PiiJsonScanner.ScanValues(null, redact);
        }

        return PiiJsonScanner.Merge(
            PiiJsonScanner.ScanValues(obj["content"], redact, ContentStructural),
            PiiJsonScanner.ScanValues(obj["structuredContent"], redact));
    }
}
