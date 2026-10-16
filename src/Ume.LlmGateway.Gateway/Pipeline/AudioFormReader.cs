using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>The parsed upload, or the error to answer with.</summary>
public sealed record AudioForm(JsonObject? Fields, AudioUpload? Audio, GatewayError? Error = null);

/// <summary>
/// Reads the <c>multipart/form-data</c> body of <c>/v1/audio/transcriptions</c> and <c>/v1/audio/translations</c>
/// (OpenAI format: a <c>file</c> part plus text fields such as <c>model</c>, <c>language</c>, <c>prompt</c>,
/// <c>response_format</c>, <c>stream</c>). The file goes into one pooled in-memory buffer: unlike
/// <c>HttpRequest.ReadFormAsync</c>, nothing is ever written to a temporary file on disk. The text fields become a
/// <see cref="JsonObject"/>, so the rest of the pipeline (model routing, PII policy on <c>prompt</c>, routing-rule
/// parameters) treats them like a JSON body.
/// </summary>
public static class AudioFormReader
{
    internal const int MaxFieldBytes = 64 * 1024;
    internal const int MaxFields = 50;

    public static async Task<AudioForm> ReadAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            return Error(GatewayError.MultipartRequired("file", "audio file"));
        }

        if (request.ContentLength > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        var fields = new JsonObject();
        byte[]? file = null;
        var fileLength = 0;
        string? fileName = null, fileType = null;
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
                    if (name != "file" || file is not null)
                    {
                        return Fail(GatewayError.OneAudioFile);
                    }

                    fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value);
                    fileType = section.ContentType;
                    (file, fileLength) = await ReadFileAsync(section.Body, request.ContentLength, maxBytes, ct);
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
            Return(file);
            return TooLarge(maxBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Fail(GatewayError.InvalidForm);
        }

        if (file is null || fileLength == 0)
        {
            return Fail(GatewayError.AudioFileMissing);
        }

        return new AudioForm(fields, new AudioUpload(file, fileLength, string.IsNullOrWhiteSpace(fileName) ? "audio" : fileName, fileType));

        // Every early exit hands the pooled file buffer back.
        AudioForm Fail(GatewayError error)
        {
            Return(file);
            return Error(error);
        }
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

    internal static async Task<string?> ReadFieldAsync(Stream body, CancellationToken ct)
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

    internal static async Task<(byte[] Buffer, int Length)> ReadFileAsync(Stream body, long? contentLength, long maxBytes, CancellationToken ct)
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

    internal static void Return(byte[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AudioForm TooLarge(long maxBytes) =>
        Error(GatewayError.AudioTooLarge(maxBytes));

    private static AudioForm Error(GatewayError error) => new(null, null, error);

    internal sealed class FileTooLargeException : Exception;
}
