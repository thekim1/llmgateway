using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// One upstream call attempt. <see cref="Body"/> is already rewritten for the target deployment. For the audio
/// endpoints it holds the form's text fields and <see cref="Audio"/> the uploaded file.
/// </summary>
public sealed record ProviderCall(
    ProviderAccount Provider,
    ModelDeployment Deployment,
    GatewayEndpoint Endpoint,
    JsonObject Body,
    bool Stream,
    string? Credential,
    bool ClientRequestedStreamUsage,
    AudioUpload? Audio = null);

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

public abstract class ProviderResult;

/// <summary>Why a provider attempt failed, which decides what the gateway does next (see <see cref="ProviderFailure"/>).</summary>
public enum FailureKind
{
    /// <summary>Overloaded, unreachable, timed out or answered garbage: try the next provider and count it against the circuit breaker.</summary>
    Transient = 0,

    /// <summary>Our configuration is wrong (upstream 401/403/404, plain HTTP in production, undecryptable credential): try the next provider.</summary>
    ProviderConfig = 1,

    /// <summary>The client's request is the problem (e.g. an invalid parameter): answer it, another provider would refuse it too.</summary>
    ClientError = 2,

    /// <summary>The provider type cannot serve this endpoint at all: try the next provider.</summary>
    Unsupported = 3,
}

/// <summary>Upstream failed before any byte was sent to the client. <see cref="Kind"/> decides fallback and circuit breaking.</summary>
public sealed class ProviderFailure(int statusCode, FailureKind kind, string reason, string? body = null, string? contentType = null) : ProviderResult
{
    /// <summary>Production refuses to send content or credentials over plain HTTP.</summary>
    public static ProviderFailure HttpsRequired { get; } = new(503, FailureKind.ProviderConfig, "https_required");

    /// <summary>The stored credential cannot be decrypted (Data Protection key ring missing?).</summary>
    public static ProviderFailure CredentialUnavailable { get; } = new(500, FailureKind.ProviderConfig, "credential_unavailable");

    public static ProviderFailure EndpointNotSupported { get; } = new(400, FailureKind.Unsupported, "endpoint_not_supported");

    public static ProviderFailure Timeout { get; } = new(504, FailureKind.Transient, "timeout");

    public static ProviderFailure InvalidJson { get; } = new(502, FailureKind.Transient, "invalid_json");

    public int StatusCode { get; } = statusCode;
    public FailureKind Kind { get; } = kind;
    public string Reason { get; } = reason;
    public string? Body { get; } = body;
    public string? ContentType { get; } = contentType;

    /// <summary>Everything but a client error is worth another provider.</summary>
    public bool CanFallBack => Kind != FailureKind.ClientError;

    /// <summary>Only transient failures say something about the provider's health.</summary>
    public bool CountsAgainstCircuit => Kind == FailureKind.Transient;

    /// <summary>What an upstream HTTP error status means.</summary>
    public static FailureKind KindForStatus(int status) => status switch
    {
        408 or 409 or 429 or >= 500 => FailureKind.Transient,
        401 or 403 or 404 => FailureKind.ProviderConfig,
        _ => FailureKind.ClientError,
    };

    /// <summary>A failure for an upstream HTTP error status, with the provider's error body when there is one.</summary>
    public static ProviderFailure FromStatus(int status, string? body = null, string? contentType = null) =>
        new(status, KindForStatus(status), $"http_{status}", body, contentType);
}

/// <summary>
/// A complete JSON answer, held as the UTF-8 bytes to send to the client so it is never re-serialised. When
/// <c>pooled</c> is given, <see cref="Body"/> lives in that rented array and <see cref="Dispose"/> returns it.
/// Transcriptions may also be plain text, SRT or WebVTT; <see cref="ContentType"/> is then the provider's.
/// </summary>
public sealed class ProviderJsonResult(int statusCode, ReadOnlyMemory<byte> body, TokenUsage usage, byte[]? pooled = null, string? contentType = null) : ProviderResult, IDisposable
{
    private byte[]? _pooled = pooled;

    public int StatusCode { get; } = statusCode;
    public ReadOnlyMemory<byte> Body { get; } = body;
    public TokenUsage Usage { get; } = usage;

    /// <summary>Null means <c>application/json</c>.</summary>
    public string? ContentType { get; } = contentType;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pooled, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Streaming response. Usage is populated while <see cref="Events"/> is enumerated.</summary>
public sealed class ProviderStreamResult(IAsyncEnumerable<SseEvent> events, UsageAccumulator usage, IDisposable? owner) : ProviderResult, IDisposable
{
    public IAsyncEnumerable<SseEvent> Events { get; } = events;
    public UsageAccumulator Usage { get; } = usage;

    public void Dispose() => owner?.Dispose();
}

public readonly record struct SseEvent(string? EventName, string Data)
{
    public const string Done = "[DONE]";

    public string Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteTo(buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes the event in SSE wire format as UTF-8 straight into <paramref name="writer"/> (no intermediate string).</summary>
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (EventName is not null)
        {
            writer.Write("event: "u8);
            Encoding.UTF8.GetBytes(EventName, writer);
            writer.Write("\n"u8);
        }

        var data = Data.AsSpan();
        while (true)
        {
            var newline = data.IndexOf('\n');
            writer.Write("data: "u8);
            Encoding.UTF8.GetBytes(newline < 0 ? data : data[..newline], writer);
            writer.Write("\n"u8);
            if (newline < 0)
            {
                break;
            }

            data = data[(newline + 1)..];
        }

        writer.Write("\n"u8);
    }
}

public sealed class UsageAccumulator
{
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Seconds of audio, when a speech-to-text provider reports usage by duration.</summary>
    public decimal AudioSeconds { get; set; }

    /// <summary>Audio part of <see cref="InputTokens"/> (gpt-4o-transcribe, audio-capable chat models).</summary>
    public long AudioInputTokens { get; set; }

    /// <summary>Audio part of <see cref="OutputTokens"/>.</summary>
    public long AudioOutputTokens { get; set; }

    public bool Reported { get; set; }

    /// <summary>Characters of streamed output, used to estimate tokens if the provider never reports usage.</summary>
    public long OutputCharacters { get; set; }

    /// <summary>The reported usage, or estimates for what was not reported (audio length comes from the uploaded file).</summary>
    public TokenUsage ToTokenUsage(long estimatedInputTokens, decimal estimatedAudioSeconds = 0) => Reported
        ? new TokenUsage(InputTokens, CachedInputTokens, OutputTokens, AudioSeconds > 0 ? AudioSeconds : estimatedAudioSeconds, AudioInputTokens, AudioOutputTokens)
        : new TokenUsage(estimatedInputTokens, 0, OutputCharacters == 0 ? 0 : CostCalculator.EstimateTokens((int)Math.Min(int.MaxValue, OutputCharacters)), estimatedAudioSeconds);
}

public interface IProviderAdapter
{
    bool CanHandle(ProviderType type);
    Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken);
}

public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        // Almost every event has a single data line: keep it as one substring and only use the builder for more.
        string? data = null;
        StringBuilder? multiline = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    yield return new SseEvent(eventName, multiline is { Length: > 0 } ? multiline.ToString() : data);
                }

                eventName = null;
                data = null;
                multiline?.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line.AsSpan() : line.AsSpan(0, colon);
            var valueStart = colon < 0 ? line.Length : colon + 1;
            if (valueStart < line.Length && line[valueStart] == ' ')
            {
                valueStart++;
            }

            if (field.SequenceEqual("data"))
            {
                if (data is null)
                {
                    data = line[valueStart..];
                }
                else
                {
                    multiline ??= new StringBuilder();
                    if (multiline.Length == 0)
                    {
                        multiline.Append(data);
                    }

                    multiline.Append('\n').Append(line, valueStart, line.Length - valueStart);
                }
            }
            else if (field.SequenceEqual("event"))
            {
                eventName = line[valueStart..];
            }
        }

        if (data is not null)
        {
            yield return new SseEvent(eventName, multiline is { Length: > 0 } ? multiline.ToString() : data);
        }
    }
}

/// <summary>Extracts usage from provider responses (OpenAI chat/embeddings/responses/transcriptions and Anthropic formats).</summary>
public static class UsageParser
{
    public static bool TryRead(JsonNode? usage, out TokenUsage result)
    {
        result = default;
        if (usage is not JsonObject u)
        {
            return false;
        }

        // Speech to text billed by duration: {"type":"duration","seconds":12.5} (whisper-1, vLLM)
        if (u["seconds"] is JsonValue seconds && seconds.TryGetValue<decimal>(out var s))
        {
            result = new TokenUsage(0, 0, 0, Math.Max(s, 0));
            return true;
        }

        // OpenAI chat / embeddings
        if (u["prompt_tokens"] is not null)
        {
            var details = u["prompt_tokens_details"];
            result = new TokenUsage(Long(u["prompt_tokens"]), Long(details?["cached_tokens"]), Long(u["completion_tokens"]),
                AudioInputTokens: Long(details?["audio_tokens"]), AudioOutputTokens: Long(u["completion_tokens_details"]?["audio_tokens"]));
            return true;
        }

        // Anthropic (input excludes cache reads/writes)
        if (u["cache_read_input_tokens"] is not null || u["cache_creation_input_tokens"] is not null)
        {
            var cacheRead = Long(u["cache_read_input_tokens"]);
            var input = Long(u["input_tokens"]) + cacheRead + Long(u["cache_creation_input_tokens"]);
            result = new TokenUsage(input, cacheRead, Long(u["output_tokens"]));
            return true;
        }

        // OpenAI Responses API / Anthropic without cache fields / Realtime and gpt-4o-transcribe (input_token_details)
        if (u["input_tokens"] is not null || u["output_tokens"] is not null)
        {
            var details = u["input_tokens_details"] ?? u["input_token_details"];
            var outputDetails = u["output_tokens_details"] ?? u["output_token_details"];
            result = new TokenUsage(Long(u["input_tokens"]), Long(details?["cached_tokens"]), Long(u["output_tokens"]),
                AudioInputTokens: Long(details?["audio_tokens"]), AudioOutputTokens: Long(outputDetails?["audio_tokens"]));
            return true;
        }

        return false;
    }

    public static void Apply(UsageAccumulator acc, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(acc);
        acc.InputTokens = usage.InputTokens;
        acc.CachedInputTokens = usage.CachedInputTokens;
        acc.OutputTokens = usage.OutputTokens;
        acc.AudioSeconds = usage.AudioSeconds;
        acc.AudioInputTokens = usage.AudioInputTokens;
        acc.AudioOutputTokens = usage.AudioOutputTokens;
        acc.Reported = true;
    }

    internal static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}

/// <summary>
/// The adapter for each provider type, decided once from the registered adapters (the first that can handle a type
/// wins, in registration order), so the request path is an array access.
/// </summary>
public sealed class ProviderAdapterLookup
{
    private readonly IProviderAdapter?[] _byType;

    public ProviderAdapterLookup(IEnumerable<IProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        IProviderAdapter[] registered = [.. adapters];
        _byType = new IProviderAdapter?[Enum.GetValues<ProviderType>().Max(t => (int)t) + 1];
        for (var type = 0; type < _byType.Length; type++)
        {
            _byType[type] = registered.FirstOrDefault(a => a.CanHandle((ProviderType)type));
        }
    }

    public IProviderAdapter Resolve(ProviderType type) =>
        (uint)type < (uint)_byType.Length && _byType[(int)type] is { } adapter
            ? adapter
            : throw new InvalidOperationException($"No provider adapter handles provider type {type}.");
}
