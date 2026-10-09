using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// Finds inline file parts (images, documents, audio) in OpenAI Chat Completions, OpenAI Responses and Anthropic
/// Messages request bodies, and decides whether a key's <see cref="AttachmentPolicy"/> lets them through.
/// </summary>
public static class AttachmentScanner
{
    /// <summary>Request fields that carry message content. Tool schemas and other parameters are not walked.</summary>
    private static readonly string[] ContentFields = ["messages", "input", "system"];

    public static AttachmentKinds Scan(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = AttachmentKinds.None;
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

    /// <summary>Content part type → kind. Covers the three request formats the gateway accepts.</summary>
    public static AttachmentKinds KindOf(string? partType) => partType switch
    {
        "image_url" or "input_image" or "image" => AttachmentKinds.Image,
        "file" or "input_file" or "document" or "container_upload" => AttachmentKinds.Document,
        "input_audio" or "audio" => AttachmentKinds.Audio,
        _ => AttachmentKinds.None,
    };

    private static void Walk(JsonNode? node, ref AttachmentKinds found)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var partType))
                {
                    var kind = KindOf(partType);
                    if (kind != AttachmentKinds.None)
                    {
                        found |= kind;
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
}
