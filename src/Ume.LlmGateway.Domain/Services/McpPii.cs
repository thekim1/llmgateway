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

    public static PiiScanResult ScanArguments(JsonObject? arguments, bool redact) => PiiJsonScanner.ScanValues(arguments, redact);

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
