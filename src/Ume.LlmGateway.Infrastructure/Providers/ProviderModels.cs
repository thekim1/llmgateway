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

public sealed class ProviderJsonResult(int statusCode, JsonNode body, TokenUsage usage) : ProviderResult
{
    public int StatusCode { get; } = statusCode;
    public JsonNode Body { get; } = body;
    public TokenUsage Usage { get; } = usage;
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
        var sb = new StringBuilder();
        if (EventName is not null)
        {
            sb.Append("event: ").Append(EventName).Append('\n');
        }

        foreach (var line in Data.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        return sb.Append('\n').ToString();
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
        var data = new StringBuilder();
        var hasData = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    hasData = true;
                    break;
            }
        }

        if (hasData)
        {
            yield return new SseEvent(eventName, data.ToString());
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
