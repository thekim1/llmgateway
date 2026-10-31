namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Price per one million tokens in USD, valid from <see cref="EffectiveFrom"/>. Image models billed per picture use
/// <see cref="PerImageUsd"/>. Speech-to-text models billed by
/// duration (Whisper, gpt-realtime-translate) use <see cref="AudioPerMinuteUsd"/> instead; a model may have both.
/// Audio tokens (gpt-realtime, gpt-4o-transcribe) cost more than text tokens: when an audio token price is set, the
/// audio part of the tokens is billed at it and only the rest at the text price.
/// </summary>
public sealed class ModelPrice
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ModelDeploymentId { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public decimal InputPerMillionUsd { get; set; }
    public decimal CachedInputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }

    /// <summary>
    /// Price per million input tokens written to the prompt cache in USD (Anthropic: 1.25x input for the 5-minute
    /// cache, 2x for the 1-hour cache). Null bills cache writes at <see cref="InputPerMillionUsd"/>.
    /// </summary>
    public decimal? CacheWriteInputPerMillionUsd { get; set; }

    /// <summary>Price per minute of input audio in USD.</summary>
    public decimal AudioPerMinuteUsd { get; set; }

    /// <summary>Price per million audio input tokens in USD; 0 bills them as text input.</summary>
    public decimal AudioInputPerMillionUsd { get; set; }

    /// <summary>Price per million audio output tokens in USD; 0 bills them as text output.</summary>
    public decimal AudioOutputPerMillionUsd { get; set; }

    /// <summary>Price per generated image in USD (DALL·E, FLUX …). Token-billed image models (gpt-image-1) use the token prices instead; a model may have both.</summary>
    public decimal PerImageUsd { get; set; }

    /// <summary>Where the price came from, e.g. <c>models.dev anthropic/claude-opus-4-5</c>; null when an admin entered it.</summary>
    public string? Source { get; set; }
}
