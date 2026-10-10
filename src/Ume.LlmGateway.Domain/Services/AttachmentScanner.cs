using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>What <see cref="AttachmentScanner.Inspect"/> found in a request body.</summary>
/// <param name="Kinds">Kinds of file parts present.</param>
/// <param name="Images">Number of image parts.</param>
/// <param name="ImageChars">Characters in the image parts' string values (base64 data or URLs).</param>
public readonly record struct AttachmentScan(AttachmentKinds Kinds, int Images, long ImageChars);

/// <summary>
/// Finds inline file parts (images, documents, audio, video) in OpenAI Chat Completions, OpenAI Responses and
/// Anthropic Messages request bodies, and decides whether a key's <see cref="AttachmentPolicy"/> lets them through.
/// </summary>
public static class AttachmentScanner
{
    /// <summary>Request fields that carry message content. Tool schemas and other parameters are not walked.</summary>
    private static readonly string[] ContentFields = ["messages", "input", "system"];

    public static AttachmentKinds Scan(JsonObject body) => Inspect(body).Kinds;

    public static AttachmentScan Inspect(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = new AttachmentScan();
        foreach (var field in ContentFields)
        {
            if (body[field] is JsonArray or JsonObject)
            {
                Walk(body[field], ref found);
            }
        }

        return found;
    }

    /// <summary>The kinds the policy refuses among those found; <see cref="AttachmentKinds.None"/> means forward.</summary>
    public static AttachmentKinds Disallowed(AttachmentPolicy policy, AttachmentKinds found) => policy switch
    {
        AttachmentPolicy.ImagesOnly => found & ~AttachmentKinds.Image,
        AttachmentPolicy.None => found,
        _ => AttachmentKinds.None,
    };

    /// <summary>
    /// Content part type → kind. Covers the three request formats the gateway accepts, plus <c>video_url</c>
    /// (vLLM and other OpenAI-compatible servers for video models).
    /// </summary>
    public static AttachmentKinds KindOf(string? partType) => partType switch
    {
        "image_url" or "input_image" or "image" => AttachmentKinds.Image,
        "file" or "input_file" or "document" or "container_upload" => AttachmentKinds.Document,
        "input_audio" or "audio" => AttachmentKinds.Audio,
        "video_url" or "input_video" or "video" => AttachmentKinds.Video,
        _ => AttachmentKinds.None,
    };

    private static void Walk(JsonNode? node, ref AttachmentScan found)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var partType))
                {
                    var kind = KindOf(partType);
                    if (kind != AttachmentKinds.None)
                    {
                        found = kind == AttachmentKinds.Image
                            ? found with { Kinds = found.Kinds | kind, Images = found.Images + 1, ImageChars = found.ImageChars + StringChars(obj) }
                            : found with { Kinds = found.Kinds | kind };
                        return; // the payload itself (base64, URL, file id) needs no further walking
                    }
                }

                foreach (var (_, child) in obj)
                {
                    if (child is JsonArray or JsonObject)
                    {
                        Walk(child, ref found); // nested content, e.g. tool_result blocks
                    }
                }

                break;

            case JsonArray arr:
                foreach (var item in arr)
                {
                    Walk(item, ref found);
                }

                break;
        }
    }

    /// <summary>Measured on the parsed UTF-8 where possible, so a multi-megabyte base64 string is not copied.</summary>
    private static long StringChars(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(p => StringChars(p.Value)),
        JsonArray arr => arr.Sum(StringChars),
        JsonValue v when v.TryGetValue<JsonElement>(out var e) => e.ValueKind == JsonValueKind.String ? JsonMarshal.GetRawUtf8Value(e).Length - 2 : 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
        _ => 0,
    };
}
