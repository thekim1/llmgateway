using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Usage;

namespace Ume.LlmGateway.Infrastructure.Providers;

public sealed class UsageAccumulator
{
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Part of <see cref="InputTokens"/> written to the prompt cache (Anthropic).</summary>
    public long CacheWriteInputTokens { get; set; }

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
        ? new TokenUsage(InputTokens, CachedInputTokens, OutputTokens, AudioSeconds > 0 ? AudioSeconds : estimatedAudioSeconds, AudioInputTokens, AudioOutputTokens,
            CacheWriteInputTokens: CacheWriteInputTokens)
        : new TokenUsage(estimatedInputTokens, 0, OutputCharacters == 0 ? 0 : CostCalculator.EstimateTokens((int)Math.Min(int.MaxValue, OutputCharacters)), estimatedAudioSeconds);
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
            var cacheWrite = Long(u["cache_creation_input_tokens"]);
            var input = Long(u["input_tokens"]) + cacheRead + cacheWrite;
            result = new TokenUsage(input, cacheRead, Long(u["output_tokens"]), CacheWriteInputTokens: cacheWrite);
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
        acc.CacheWriteInputTokens = usage.CacheWriteInputTokens;
        acc.OutputTokens = usage.OutputTokens;
        acc.AudioSeconds = usage.AudioSeconds;
        acc.AudioInputTokens = usage.AudioInputTokens;
        acc.AudioOutputTokens = usage.AudioOutputTokens;
        acc.Reported = true;
    }

    internal static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}
