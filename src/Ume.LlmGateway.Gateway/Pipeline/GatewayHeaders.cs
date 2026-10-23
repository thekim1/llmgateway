using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Response headers the gateway adds (documented in docs/admin-api.md and the README).</summary>
public static class GatewayHeaders
{
    /// <summary>Provider account that answered.</summary>
    public const string Provider = "x-ume-provider";

    /// <summary>Model deployment that answered.</summary>
    public const string Model = "x-ume-model";

    /// <summary>Data residency of the provider that answered.</summary>
    public const string Residency = "x-ume-residency";

    /// <summary>Providers tried before the one that answered (or all of them, when every one failed).</summary>
    public const string Fallbacks = "x-ume-fallbacks";

    /// <summary>Cost of the request in USD, the currency it is calculated in (complete JSON answers only).</summary>
    public const string CostUsd = "x-ume-cost-usd";

    /// <summary>Smallest remaining amount over the budgets that apply, in USD.</summary>
    public const string BudgetRemainingUsd = "x-ume-budget-remaining-usd";

    /// <summary><see cref="CostUsd"/> in SEK at the current exchange rate (display only; to be removed with SEK).</summary>
    public const string CostSek = "x-ume-cost-sek";

    /// <summary><see cref="BudgetRemainingUsd"/> in SEK at the current exchange rate (display only; to be removed with SEK).</summary>
    public const string BudgetRemainingSek = "x-ume-budget-remaining-sek";

    /// <summary>Ids of the routing rules that decided where the request went, comma separated.</summary>
    public const string Rule = "x-ume-rule";

    /// <summary>Response header: <c>flagged</c> when the content guard flagged the request or answer (policy Record).</summary>
    public const string Guard = "x-ume-guard";

    /// <summary>Request header: a session or conversation id that keeps the session on one target (prompt cache reuse).</summary>
    public const string Session = "x-ume-session";

    /// <summary>What the PII guard did: detected, redacted, blocked or rerouted-onprem.</summary>
    public const string Pii = "x-ume-pii";

    public const string RateLimitLimitRequests = "x-ratelimit-limit-requests";
    public const string RateLimitRemainingRequests = "x-ratelimit-remaining-requests";
}

/// <summary>Model names as recorded and echoed back: clients may send anything, so the length is capped.</summary>
public static class ModelNames
{
    /// <summary>Length of <c>UsageRecord.RequestedModel</c> and of names quoted in error messages.</summary>
    public const int MaxLength = 200;

    /// <summary>The name as logged and stored: at most <see cref="MaxLength"/> characters, control characters replaced.</summary>
    public static string Truncate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecurityEvents.Printable(name.Length > MaxLength ? name[..MaxLength] : name);
    }
}
