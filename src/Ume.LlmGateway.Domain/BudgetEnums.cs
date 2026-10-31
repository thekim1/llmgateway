namespace Ume.LlmGateway.Domain;

public enum BudgetScope
{
    Department = 0,
    Team = 1,
    VirtualKey = 2,
}

/// <summary>What a budget counts.</summary>
public enum BudgetUnit
{
    /// <summary>Cost in USD (<see cref="Entities.Budget.LimitUsd"/>).</summary>
    Usd = 0,

    /// <summary>
    /// Input plus output tokens (<see cref="Entities.Budget.LimitTokens"/>): a quota for models whose cost is zero, such as
    /// on-prem ones, so their capacity is shared fairly.
    /// </summary>
    Tokens = 1,
}

public enum BudgetPeriod
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4,
    Hourly = 5,
}
