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

    /// <summary>Cost of the request in SEK (complete JSON answers only).</summary>
    public const string CostSek = "x-ume-cost-sek";

    /// <summary>Smallest remaining amount over the budgets that apply, in SEK.</summary>
    public const string BudgetRemainingSek = "x-ume-budget-remaining-sek";

    /// <summary>Ids of the routing rules that decided where the request went, comma separated.</summary>
    public const string Rule = "x-ume-rule";

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

    public static string Truncate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length > MaxLength ? name[..MaxLength] : name;
    }
}
