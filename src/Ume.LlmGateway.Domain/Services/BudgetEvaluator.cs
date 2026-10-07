using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>A budget together with how much has been spent (and reserved) in its current period.</summary>
public sealed record BudgetState(Budget Budget, PeriodWindow Window, decimal SpentSek);

public sealed record BudgetDecision(bool Allowed, BudgetState? ExceededBudget)
{
    public static readonly BudgetDecision Allow = new(true, null);
}

public static class BudgetEvaluator
{
    /// <summary>
    /// Hard limit: a request is rejected if ANY budget up the hierarchy (key → team → förvaltning)
    /// has reached 100 % of its limit.
    /// </summary>
    public static BudgetDecision Evaluate(IEnumerable<BudgetState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var exceeded = states
            .Where(s => s.Budget.IsActive && s.SpentSek >= s.Budget.LimitSek)
            .OrderBy(s => s.Budget.Scope == BudgetScope.VirtualKey ? 0 : s.Budget.Scope == BudgetScope.Team ? 1 : 2)
            .FirstOrDefault();
        return exceeded is null ? BudgetDecision.Allow : new BudgetDecision(false, exceeded);
    }

    /// <summary>Returns the alert thresholds crossed when spend moved from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<int> CrossedThresholds(Budget budget, decimal before, decimal after)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.LimitSek <= 0 || after <= before)
        {
            return [];
        }

        return budget.AlertThresholds
            .Where(t => t is > 0 and <= 1000)
            .Distinct()
            .Order()
            .Where(t =>
            {
                var level = budget.LimitSek * t / 100m;
                return before < level && after >= level;
            })
            .ToList();
    }

    public static decimal PercentUsed(decimal spent, decimal limit) =>
        limit <= 0 ? 0 : Math.Round(spent / limit * 100m, 1, MidpointRounding.AwayFromZero);
}
