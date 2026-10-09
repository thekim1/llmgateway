using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>One upstream call attempt. <see cref="Body"/> is already rewritten for the target deployment.</summary>
public sealed record ProviderCall(
    ProviderAccount Provider,
    ModelDeployment Deployment,
    GatewayEndpoint Endpoint,
    JsonObject Body,
    bool Stream,
    string? Credential,
    bool ClientRequestedStreamUsage);

public abstract class ProviderResult;

/// <summary>Upstream failed before any byte was sent to the client. Retryable failures trigger fallback.</summary>
public sealed class ProviderFailure(int statusCode, bool retryable, string reason, string? body = null, string? contentType = null) : ProviderResult
{
    public int StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;
    public string Reason { get; } = reason;
    public string? Body { get; } = body;
    public string? ContentType { get; } = contentType;

    public static bool IsRetryableStatus(int status) => status is 408 or 409 or 429 or >= 500;
}

/// <summary>
/// A complete JSON answer, held as the UTF-8 bytes to send to the client so it is never re-serialised. When
/// <c>pooled</c> is given, <see cref="Body"/> lives in that rented array and <see cref="Dispose"/> returns it.
/// </summary>
public sealed class ProviderJsonResult(int statusCode, ReadOnlyMemory<byte> body, TokenUsage usage, byte[]? pooled = null) : ProviderResult, IDisposable
{
    private byte[]? _pooled = pooled;

    public int StatusCode { get; } = statusCode;
    public ReadOnlyMemory<byte> Body { get; } = body;
    public TokenUsage Usage { get; } = usage;

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
    public bool Reported { get; set; }

    /// <summary>Characters of streamed output, used to estimate tokens if the provider never reports usage.</summary>
    public long OutputCharacters { get; set; }

    public TokenUsage ToTokenUsage(long estimatedInputTokens) => Reported
        ? new TokenUsage(InputTokens, CachedInputTokens, OutputTokens)
        : new TokenUsage(estimatedInputTokens, 0, OutputCharacters == 0 ? 0 : CostCalculator.EstimateTokens((int)Math.Min(int.MaxValue, OutputCharacters)));
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

/// <summary>Extracts token usage from provider responses (OpenAI chat/embeddings/responses and Anthropic formats).</summary>
public static class UsageParser
{
    public static bool TryRead(JsonNode? usage, out TokenUsage result)
    {
        result = default;
        if (usage is not JsonObject u)
        {
            return false;
        }

        // OpenAI chat / embeddings
        if (u["prompt_tokens"] is not null)
        {
            var cached = Long(u["prompt_tokens_details"]?["cached_tokens"]);
            result = new TokenUsage(Long(u["prompt_tokens"]), cached, Long(u["completion_tokens"]));
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

        // OpenAI Responses API / Anthropic without cache fields
        if (u["input_tokens"] is not null || u["output_tokens"] is not null)
        {
            var cached = Long(u["input_tokens_details"]?["cached_tokens"]);
            result = new TokenUsage(Long(u["input_tokens"]), cached, Long(u["output_tokens"]));
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
        acc.Reported = true;
    }

    internal static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}
