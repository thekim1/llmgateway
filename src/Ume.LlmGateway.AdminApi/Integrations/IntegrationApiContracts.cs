using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Integrations;

// The Integration API's responses (docs/integrations.md), for platforms; names are camelCase like the rest of the admin API.

/// <summary>What <c>GET /integration/v1/usage</c> groups by.</summary>
public enum UsageGrouping
{
    Day = 0,
    Month = 1,
}

/// <summary>
/// One row of usage: the period (Europe/Stockholm date it starts), and the key, model and step when the request groups by
/// them (null otherwise, and step null for usage summarised by month before the retention boundary).
/// </summary>
public sealed record IntegrationUsageRow(
    DateOnly Period, Guid? KeyId, string? KeyName, string? Model, string? Step, long Requests, long SuccessfulRequests, long InputTokens,
    long CachedInputTokens, long OutputTokens, decimal CostUsd);

public sealed record IntegrationUsageDto(DateOnly From, DateOnly To, string Granularity, IReadOnlyList<string> GroupBy, IReadOnlyList<IntegrationUsageRow> Items);

/// <summary>
/// A budget that applies to a key: its own (and its rotation predecessors'), its team's and its department's. <c>spent</c> is
/// in the budget's unit (USD, or tokens for a token quota) in the current period.
/// </summary>
public sealed record IntegrationBudgetDto(
    Guid Id, BudgetScope Scope, Guid ScopeId, BudgetUnit Unit, decimal Limit, DataResidency? Residency, BudgetPeriod Period,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, decimal Spent, decimal PercentUsed, bool Exhausted);

public sealed record IntegrationBudgetsDto(Guid KeyId, IReadOnlyList<IntegrationBudgetDto> Items);
