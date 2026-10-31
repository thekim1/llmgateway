namespace Ume.LlmGateway.Domain.Entities;

public sealed class AlertEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BudgetId { get; set; }
    public BudgetScope Scope { get; set; }
    public Guid ScopeId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public int ThresholdPercent { get; set; }
    public decimal SpentUsd { get; set; }
    public decimal LimitUsd { get; set; }

    /// <summary>Set for alerts of token budgets (their <see cref="SpentUsd"/> and <see cref="LimitUsd"/> are 0).</summary>
    public long? SpentTokens { get; set; }

    /// <summary>Set for alerts of token budgets.</summary>
    public long? LimitTokens { get; set; }

    public DateTimeOffset Timestamp { get; set; }
    public bool Acknowledged { get; set; }
}
