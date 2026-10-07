using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

public readonly record struct TokenUsage(long InputTokens, long CachedInputTokens, long OutputTokens)
{
    public long Total => InputTokens + OutputTokens;
}

public readonly record struct Cost(decimal Usd, decimal Sek);

public static class CostCalculator
{
    private const decimal Million = 1_000_000m;

    /// <summary>
    /// <paramref name="usage"/>.InputTokens is the total prompt tokens INCLUDING cached tokens (OpenAI semantics);
    /// cached tokens are billed at the cached rate, the rest at the input rate. Reasoning tokens are part of
    /// output tokens.
    /// </summary>
    public static Cost Calculate(TokenUsage usage, ModelPrice? price, decimal sekPerUsd)
    {
        if (price is null)
        {
            return new Cost(0, 0);
        }

        var cached = Math.Clamp(usage.CachedInputTokens, 0, Math.Max(usage.InputTokens, 0));
        var uncached = Math.Max(usage.InputTokens - cached, 0);
        var usd = (uncached * price.InputPerMillionUsd
                   + cached * price.CachedInputPerMillionUsd
                   + Math.Max(usage.OutputTokens, 0) * price.OutputPerMillionUsd) / Million;
        usd = Math.Round(usd, 8, MidpointRounding.AwayFromZero);
        var sek = Math.Round(usd * sekPerUsd, 6, MidpointRounding.AwayFromZero);
        return new Cost(usd, sek);
    }

    /// <summary>Very rough pre-flight token estimate (≈4 characters per token) used for reservations.</summary>
    public static long EstimateTokens(int characters) => Math.Max(1, characters / 4);
}
