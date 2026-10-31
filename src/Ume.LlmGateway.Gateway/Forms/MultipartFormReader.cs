using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Ume.LlmGateway.Gateway.Requests;

namespace Ume.LlmGateway.Gateway.Forms;

/// <summary>A file part of a multipart upload, in a pooled buffer the caller returns (<see cref="MultipartFormReader.Return"/>).</summary>
internal readonly record struct FormFilePart(string Field, byte[] Buffer, int Length, string? FileName, string? ContentType)
{
    public ReadOnlyMemory<byte> Data => Buffer.AsMemory(0, Length);
}

/// <summary>What one upload endpoint accepts; the shared reading is in <see cref="MultipartFormReader"/>.</summary>
/// <param name="NotMultipart">The answer when the body is not <c>multipart/form-data</c>.</param>
/// <param name="TooLarge">The answer when the files together exceed the limit (given in bytes).</param>
/// <param name="RefuseFile">Checks a file part before it is read, given its field name and the parts read so far; null accepts it.</param>
internal sealed record MultipartRules(
    GatewayError NotMultipart,
    Func<long, GatewayError> TooLarge,
    Func<string, IReadOnlyList<FormFilePart>, GatewayError?> RefuseFile);

/// <summary>
/// Reads a <c>multipart/form-data</c> body (OpenAI's audio and image-edit uploads). Files go into pooled in-memory
/// buffers: unlike <c>HttpRequest.ReadFormAsync</c>, nothing is ever written to a temporary file on disk. Text fields
/// become a <see cref="JsonObject"/>, so the rest of the pipeline (model routing, the PII policy on <c>prompt</c>,
/// routing-rule parameters) treats them like a JSON body. <c>maxBytes</c> limits all files together. On a refusal every
/// buffer has been returned; on success the caller owns the files' buffers.
/// </summary>
internal static class MultipartFormReader
{
    internal const int MaxFieldBytes = 64 * 1024;
    internal const int MaxFields = 50;

    public static async Task<(JsonObject? Fields, List<FormFilePart> Files, GatewayError? Error)> ReadAsync(
        HttpRequest request, long maxBytes, MultipartRules rules, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            return (null, [], rules.NotMultipart);
        }

        if (request.ContentLength > maxBytes)
        {
            return (null, [], rules.TooLarge(maxBytes));
        }

        var fields = new JsonObject();
        List<FormFilePart> files = [];
        var total = 0L;
        try
        {
            var reader = new MultipartReader(boundary, request.Body) { HeadersLengthLimit = 16 * 1024, BodyLengthLimit = maxBytes };
            var count = 0;
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (++count > MaxFields || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) || HeaderUtilities.RemoveQuotes(disposition.Name).Value is not { Length: > 0 } name)
                {
                    return Fail(GatewayError.InvalidForm);
                }

                if (disposition.IsFileDisposition())
                {
                    if (rules.RefuseFile(name, files) is { } refused)
                    {
                        return Fail(refused);
                    }

                    var fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value);
                    var (buffer, length) = await ReadFileAsync(section.Body, request.ContentLength, Math.Max(1, maxBytes - total), ct);
                    files.Add(new FormFilePart(name, buffer, length, string.IsNullOrWhiteSpace(fileName) ? null : fileName, section.ContentType));
                    total += length;
                    continue;
                }

                var value = await ReadFieldAsync(section.Body, ct);
                if (value is null)
                {
                    return Fail(GatewayError.FieldTooLarge(name));
                }

                Add(fields, name, value);
            }
        }
        catch (Exception ex) when (ex is FileTooLargeException
            || (ex is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
            || (ex is InvalidDataException && ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase)))
        {
            return Fail(rules.TooLarge(maxBytes));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Fail(GatewayError.InvalidForm);
        }
        catch
        {
            Return(files);
            throw;
        }

        return (fields, files, null);

        // Every refusal hands the pooled file buffers back.
        (JsonObject?, List<FormFilePart>, GatewayError?) Fail(GatewayError error)
        {
            Return(files);
            return (null, [], error);
        }
    }

    public static void Return(List<FormFilePart> files)
    {
        foreach (var file in files)
        {
            ArrayPool<byte>.Shared.Return(file.Buffer);
        }

        files.Clear();
    }

    /// <summary>
    /// Form values are strings. <c>stream</c> becomes a boolean (the pipeline reads it as one); a repeated name, or one
    /// ending in <c>[]</c>, becomes an array so each value is sent upstream again.
    /// </summary>
    internal static void Add(JsonObject fields, string name, string value)
    {
        JsonNode node = name == "stream" && bool.TryParse(value, out var flag) ? JsonValue.Create(flag) : JsonValue.Create(value);
        if (fields[name] is JsonArray values)
        {
            values.Add(node);
        }
        else if (fields.ContainsKey(name) || name.EndsWith("[]", StringComparison.Ordinal))
        {
            var existing = fields[name];
            fields.Remove(name);
            fields[name] = existing is null ? new JsonArray(node) : new JsonArray(existing, node);
        }
        else
        {
            fields[name] = node;
        }
    }

    private static async Task<string?> ReadFieldAsync(Stream body, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFieldBytes + 1);
        try
        {
            var length = 0;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(length, MaxFieldBytes + 1 - length), ct)) > 0)
            {
                length += read;
                if (length > MaxFieldBytes)
                {
                    return null;
                }
            }

            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(byte[] Buffer, int Length)> ReadFileAsync(Stream body, long? contentLength, long maxBytes, CancellationToken ct)
    {
        // Content-Length covers the whole form, so it is a slight over-size for the file: one rent, no regrowth.
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(contentLength ?? 1024 * 1024, 1, maxBytes) + 1);
        var length = 0;
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(length), ct)) > 0)
            {
                length += read;
                if (length > maxBytes)
                {
                    throw new FileTooLargeException();
                }

                if (length == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(buffer.Length * 2L, maxBytes + 1));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
            }

            return (buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private sealed class FileTooLargeException : Exception;
}
