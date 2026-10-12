using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>The parsed image edit upload, or the error to answer with.</summary>
public sealed record ImageForm(JsonObject? Fields, ImageUploads? Images, int Status = 0, string? Code = null, string? Message = null)
{
    public bool Failed => Status != 0;
}

/// <summary>
/// Reads the <c>multipart/form-data</c> body of <c>/v1/images/edits</c> (OpenAI format: one or more pictures in
/// <c>image</c> or <c>image[]</c>, an optional <c>mask</c>, and text fields such as <c>model</c>, <c>prompt</c>, <c>n</c>,
/// <c>size</c>, <c>quality</c>). Like <see cref="AudioFormReader"/>, the files go into pooled in-memory buffers and
/// never to a temporary file, and the text fields become a <see cref="JsonObject"/> so the rest of the pipeline treats
/// them like a JSON body. <c>maxBytes</c> limits all files together.
/// </summary>
public static class ImageFormReader
{
    /// <summary>gpt-image-1 takes up to 16 pictures to combine.</summary>
    public const int MaxImages = 16;

    public static async Task<ImageForm> ReadAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            return Error(415, GatewayErrorCodes.UnsupportedMediaType, "Content-Type måste vara multipart/form-data med bilderna i fältet 'image'.");
        }

        if (request.ContentLength > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        var fields = new JsonObject();
        List<ImageFile> files = [];
        List<byte[]> pooled = [];
        var total = 0L;
        try
        {
            var reader = new MultipartReader(boundary, request.Body) { HeadersLengthLimit = 16 * 1024, BodyLengthLimit = maxBytes };
            var count = 0;
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (++count > AudioFormReader.MaxFields || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) || HeaderUtilities.RemoveQuotes(disposition.Name).Value is not { Length: > 0 } name)
                {
                    return Fail(400, GatewayErrorCodes.InvalidRequest, "Formuläret är inte giltigt multipart/form-data.");
                }

                if (disposition.IsFileDisposition())
                {
                    if (name is not ("image" or "image[]" or "mask") || (name == "mask" && files.Any(f => f.Field == "mask")) || files.Count >= MaxImages + 1)
                    {
                        return Fail(400, GatewayErrorCodes.InvalidRequest, $"Skicka bilderna i fältet 'image' (högst {MaxImages}) och en eventuell mask i fältet 'mask'.");
                    }

                    var fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value);
                    var (buffer, length) = await AudioFormReader.ReadFileAsync(section.Body, request.ContentLength, Math.Max(1, maxBytes - total), ct);
                    pooled.Add(buffer);
                    total += length;
                    if (length > 0)
                    {
                        files.Add(new ImageFile(name, buffer.AsMemory(0, length), string.IsNullOrWhiteSpace(fileName) ? "image" : fileName, section.ContentType));
                    }

                    continue;
                }

                var value = await AudioFormReader.ReadFieldAsync(section.Body, ct);
                if (value is null)
                {
                    return Fail(400, GatewayErrorCodes.InvalidRequest, $"Fältet '{(name.Length > 50 ? name[..50] : name)}' är för stort.");
                }

                AudioFormReader.Add(fields, name, value);
            }
        }
        catch (Exception ex) when (ex is AudioFormReader.FileTooLargeException
            || (ex is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
            || (ex is InvalidDataException && ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase)))
        {
            ReturnAll();
            return TooLarge(maxBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Fail(400, GatewayErrorCodes.InvalidRequest, "Formuläret är inte giltigt multipart/form-data.");
        }
        catch
        {
            ReturnAll();
            throw;
        }

        if (!files.Any(f => f.Field != "mask"))
        {
            return Fail(400, GatewayErrorCodes.InvalidRequest, "Bilden saknas. Skicka den i fältet 'image' (png, jpg eller webp).");
        }

        return new ImageForm(fields, new ImageUploads(files, pooled));

        // Every early exit hands the pooled file buffers back.
        ImageForm Fail(int status, string code, string message)
        {
            ReturnAll();
            return Error(status, code, message);
        }

        void ReturnAll()
        {
            foreach (var buffer in pooled)
            {
                AudioFormReader.Return(buffer);
            }

            pooled.Clear();
        }
    }

    private static ImageForm TooLarge(long maxBytes) =>
        Error(413, GatewayErrorCodes.RequestTooLarge, $"Bilderna är för stora (högst {maxBytes / (1024 * 1024)} MB tillsammans). Minska upplösningen eller komprimera dem.");

    private static ImageForm Error(int status, string code, string message) => new(null, null, status, code, message);
}
