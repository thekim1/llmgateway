using System.Text.Json.Nodes;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>The parsed upload, or the error to answer with.</summary>
public sealed record AudioForm(JsonObject? Fields, AudioUpload? Audio, GatewayError? Error = null);

/// <summary>
/// Reads the <c>multipart/form-data</c> body of <c>/v1/audio/transcriptions</c> and <c>/v1/audio/translations</c>
/// (OpenAI format: a <c>file</c> part plus text fields such as <c>model</c>, <c>language</c>, <c>prompt</c>,
/// <c>response_format</c>, <c>stream</c>) with <see cref="MultipartFormReader"/>.
/// </summary>
public static class AudioFormReader
{
    private static readonly MultipartRules Rules = new(
        GatewayError.MultipartRequired("file", "audio file"),
        GatewayError.AudioTooLarge,
        static (name, files) => name != "file" || files.Count > 0 ? GatewayError.OneAudioFile : null);

    public static async Task<AudioForm> ReadAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (fields, files, error) = await MultipartFormReader.ReadAsync(request, maxBytes, Rules, ct);
        if (error is not null)
        {
            return new AudioForm(null, null, error);
        }

        if (files is not [{ Length: > 0 } file])
        {
            MultipartFormReader.Return(files);
            return new AudioForm(null, null, GatewayError.AudioFileMissing);
        }

        return new AudioForm(fields, new AudioUpload(file.Buffer, file.Length, file.FileName ?? "audio", file.ContentType));
    }
}
