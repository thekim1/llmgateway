using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// What a request consumed. <paramref name="AudioSeconds"/> is set by the audio and realtime endpoints only.
/// <paramref name="AudioInputTokens"/> and <paramref name="AudioOutputTokens"/> are the audio part of the input and
/// output tokens (already included in them), as reported by audio-capable models.
/// </summary>
public readonly record struct TokenUsage(long InputTokens, long CachedInputTokens, long OutputTokens, decimal AudioSeconds = 0,
    long AudioInputTokens = 0, long AudioOutputTokens = 0)
{
    public long Total => InputTokens + OutputTokens;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens, a.CachedInputTokens + b.CachedInputTokens, a.OutputTokens + b.OutputTokens,
        a.AudioSeconds + b.AudioSeconds, a.AudioInputTokens + b.AudioInputTokens, a.AudioOutputTokens + b.AudioOutputTokens);
}

public readonly record struct Cost(decimal Usd, decimal Sek);

public static class CostCalculator
{
    private const decimal Million = 1_000_000m;

    /// <summary>
    /// <paramref name="usage"/>.InputTokens is the total prompt tokens INCLUDING cached tokens (OpenAI semantics);
    /// cached tokens are billed at the cached rate, the rest at the input rate. Reasoning tokens are part of
    /// output tokens. Audio tokens are billed at the audio token prices when those are set (otherwise as text), and
    /// audio duration per minute on top, for models priced that way.
    /// </summary>
    public static Cost Calculate(TokenUsage usage, ModelPrice? price, decimal sekPerUsd)
    {
        if (price is null)
        {
            return new Cost(0, 0);
        }

        var cached = Math.Clamp(usage.CachedInputTokens, 0, Math.Max(usage.InputTokens, 0));
        var uncached = Math.Max(usage.InputTokens - cached, 0);
        var output = Math.Max(usage.OutputTokens, 0);
        var audioIn = price.AudioInputPerMillionUsd > 0 ? Math.Clamp(usage.AudioInputTokens, 0, uncached) : 0;
        var audioOut = price.AudioOutputPerMillionUsd > 0 ? Math.Clamp(usage.AudioOutputTokens, 0, output) : 0;
        var usd = ((uncached - audioIn) * price.InputPerMillionUsd
                   + audioIn * price.AudioInputPerMillionUsd
                   + cached * price.CachedInputPerMillionUsd
                   + (output - audioOut) * price.OutputPerMillionUsd
                   + audioOut * price.AudioOutputPerMillionUsd) / Million
                  + Math.Max(usage.AudioSeconds, 0) / 60m * price.AudioPerMinuteUsd;
        usd = Math.Round(usd, 8, MidpointRounding.AwayFromZero);
        var sek = Math.Round(usd * sekPerUsd, 6, MidpointRounding.AwayFromZero);
        return new Cost(usd, sek);
    }

    /// <summary>Very rough pre-flight token estimate (≈4 characters per token) used for reservations.</summary>
    public static long EstimateTokens(int characters) => Math.Max(1, characters / 4);

    /// <summary>
    /// Upper bound for one image's input tokens. Providers bill images by resolution after downscaling (Claude about
    /// 1 600, GPT-4o about 1 100 at high detail), not by the size of the encoded file.
    /// </summary>
    public const int ImageTokenEstimate = 1600;

    /// <summary>
    /// Pre-flight estimate for a request body: text at ≈4 bytes per token, images at <see cref="ImageTokenEstimate"/>
    /// each instead of their base64 length (a 4 MB photo would otherwise count as a million tokens). Documents, audio
    /// and video stay size-based, which over-estimates them.
    /// </summary>
    public static long EstimateInputTokens(long bodyBytes, AttachmentScan attachments) =>
        EstimateTokens((int)Math.Clamp(bodyBytes - attachments.ImageChars, 0, int.MaxValue))
        + (long)attachments.Images * ImageTokenEstimate;

    /// <summary>Audio input tokens per second for token-billed speech models (gpt-4o-transcribe: about 1 000 per minute).</summary>
    public const int AudioTokensPerSecond = 17;

    /// <summary>Transcript tokens per second of speech (about 150 words a minute, Swedish tokenises at ~2 tokens a word).</summary>
    public const int TranscriptTokensPerSecond = 5;

    /// <summary>
    /// Pre-flight estimate for a transcription of <paramref name="audioSeconds"/> of audio. Covers both pricing models:
    /// per-minute (Whisper) through <see cref="TokenUsage.AudioSeconds"/>, per-token (gpt-4o-transcribe) through the
    /// token counts. Each model has one of the two prices, so the other part costs nothing.
    /// </summary>
    public static TokenUsage EstimateTranscription(decimal audioSeconds)
    {
        var audioTokens = (long)Math.Ceiling(audioSeconds * AudioTokensPerSecond);
        return new(audioTokens, 0, (long)Math.Ceiling(audioSeconds * TranscriptTokensPerSecond), audioSeconds, AudioInputTokens: audioTokens);
    }

    /// <summary>
    /// Pre-flight estimate for <paramref name="audioSeconds"/> of a live session, for the budget reservation: the
    /// transcription estimate plus as much spoken output (interpreting, voice answers), so it covers every way realtime
    /// models are priced.
    /// </summary>
    public static TokenUsage EstimateRealtime(decimal audioSeconds)
    {
        var transcription = EstimateTranscription(audioSeconds);
        var spoken = (long)Math.Ceiling(audioSeconds * AudioTokensPerSecond);
        return transcription with { OutputTokens = transcription.OutputTokens + spoken, AudioOutputTokens = spoken };
    }
}
