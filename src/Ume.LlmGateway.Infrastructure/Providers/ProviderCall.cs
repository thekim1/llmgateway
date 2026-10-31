using System.Buffers;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Usage;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// One upstream call attempt. <see cref="Body"/> is already rewritten for the target deployment. For the audio
/// endpoints it holds the form's text fields and <see cref="Audio"/> the uploaded file; for image edits the text fields
/// and <see cref="Images"/> the uploaded pictures.
/// </summary>
public sealed record ProviderCall(
    ProviderAccount Provider,
    ModelDeployment Deployment,
    GatewayEndpoint Endpoint,
    JsonObject Body,
    bool Stream,
    string? Credential,
    bool ClientRequestedStreamUsage,
    AudioUpload? Audio = null,
    ImageUploads? Images = null,
    PromptCacheMode PromptCaching = PromptCacheMode.Off);

/// <summary>
/// Prompt-cache markers the gateway adds when it translates an OpenAI-format request for Anthropic (a client using
/// the OpenAI SDK cannot add them). Cached input costs a tenth of normal input on a later hit and 1.25 times on the
/// write. Prompts below Anthropic's minimum (1 024 tokens; 2 048 for Haiku) are not cached and not charged extra.
/// </summary>
public enum PromptCacheMode
{
    /// <summary>No markers.</summary>
    Off = 0,

    /// <summary>The tool definitions and the system prompt, which an application repeats on every request.</summary>
    Static = 1,

    /// <summary>Also the conversation so far, so the next turn of a chat reads it from the cache.</summary>
    Conversation = 2,
}

/// <summary>
/// An audio file uploaded to a speech-to-text endpoint, held in memory only (never on disk) for as long as the
/// request runs, so fallback attempts can send it again. <see cref="Dispose"/> returns the pooled buffer.
/// </summary>
public sealed class AudioUpload(byte[] pooled, int length, string fileName, string? contentType) : IDisposable
{
    private byte[]? _pooled = pooled;

    public ReadOnlyMemory<byte> Data { get; } = pooled.AsMemory(0, length);
    public string FileName { get; } = fileName;
    public string? ContentType { get; } = contentType;

    /// <summary>Playing time read from the file header, or a deliberately high guess (see <see cref="AudioDuration"/>).</summary>
    public decimal EstimatedSeconds { get; } = AudioDuration.Estimate(pooled.AsSpan(0, length));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pooled, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>One picture of an image edit request: the form field it came in (<c>image</c>, <c>image[]</c> or <c>mask</c>) and its bytes.</summary>
public sealed record ImageFile(string Field, ReadOnlyMemory<byte> Data, string FileName, string? ContentType);

/// <summary>
/// The pictures uploaded to <c>/v1/images/edits</c>, held in memory only (never on disk) for as long as the request
/// runs, so fallback attempts can send them again. <see cref="Dispose"/> returns the pooled buffers.
/// </summary>
public sealed class ImageUploads(IReadOnlyList<ImageFile> files, IReadOnlyList<byte[]> pooled) : IDisposable
{
    private IReadOnlyList<byte[]>? _pooled = pooled;

    public IReadOnlyList<ImageFile> Files { get; } = files;

    /// <summary>The pictures to edit; the optional mask is not one of them.</summary>
    public int ImageCount => Files.Count(f => f.Field != "mask");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pooled, null) is { } buffers)
        {
            foreach (var buffer in buffers)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
