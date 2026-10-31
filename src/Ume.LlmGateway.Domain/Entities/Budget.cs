namespace Ume.LlmGateway.Domain.Entities;

public sealed class Budget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public BudgetScope Scope { get; set; }
    public Guid ScopeId { get; set; }

    /// <summary>What the budget counts: USD (default) or tokens.</summary>
    public BudgetUnit Unit { get; set; }

    /// <summary>The limit in USD, the currency costs are calculated and counted in (<see cref="BudgetUnit.Usd"/> budgets).</summary>
    public decimal LimitUsd { get; set; }

    /// <summary>The limit in input plus output tokens (<see cref="BudgetUnit.Tokens"/> budgets).</summary>
    public long LimitTokens { get; set; }

    /// <summary>
    /// Token budgets only: count only requests served by providers of this residency (e.g. <see cref="DataResidency.OnPrem"/>
    /// for a quota on on-prem capacity). Null counts every request.
    /// </summary>
    public DataResidency? Residency { get; set; }

    /// <summary>The limit in the budget's unit.</summary>
    public decimal Limit => Unit == BudgetUnit.Tokens ? LimitTokens : LimitUsd;

    public BudgetPeriod Period { get; set; }

    /// <summary>Percentages (1-100) at which alerts are raised.</summary>
    public List<int> AlertThresholds { get; set; } = [50, 80, 100];

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
