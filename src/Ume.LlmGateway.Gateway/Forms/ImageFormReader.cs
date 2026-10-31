using System.Text.Json.Nodes;
using Ume.LlmGateway.Gateway.Requests;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Forms;

/// <summary>The parsed image edit upload, or the error to answer with.</summary>
public sealed record ImageForm(JsonObject? Fields, ImageUploads? Images, GatewayError? Error = null);

/// <summary>
/// Reads the <c>multipart/form-data</c> body of <c>/v1/images/edits</c> (OpenAI format: one or more pictures in
/// <c>image</c> or <c>image[]</c>, an optional <c>mask</c>, and text fields such as <c>model</c>, <c>prompt</c>, <c>n</c>,
/// <c>size</c>, <c>quality</c>) with <see cref="MultipartFormReader"/>. Empty file parts are ignored.
/// </summary>
public static class ImageFormReader
{
    /// <summary>gpt-image-1 takes up to 16 pictures to combine.</summary>
    public const int MaxImages = 16;

    private static readonly MultipartRules Rules = new(
        GatewayError.MultipartRequired("image", "pictures"),
        GatewayError.ImagesTooLarge,
        static (name, files) => name is not ("image" or "image[]" or "mask")
            || (name == "mask" && files.Any(f => f.Length > 0 && f.Field == "mask"))
            || files.Count(f => f.Length > 0) >= MaxImages + 1
                ? GatewayError.ImageFields(MaxImages)
                : null);

    public static async Task<ImageForm> ReadAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (fields, parts, error) = await MultipartFormReader.ReadAsync(request, maxBytes, Rules, ct);
        if (error is not null)
        {
            return new ImageForm(null, null, error);
        }

        if (!parts.Any(f => f.Length > 0 && f.Field != "mask"))
        {
            MultipartFormReader.Return(parts);
            return new ImageForm(null, null, GatewayError.ImageMissing);
        }

        List<ImageFile> files = [.. parts.Where(f => f.Length > 0).Select(f => new ImageFile(f.Field, f.Data, f.FileName ?? "image", f.ContentType))];
        return new ImageForm(fields, new ImageUploads(files, [.. parts.Select(f => f.Buffer)]));
    }
}
