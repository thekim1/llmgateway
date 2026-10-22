using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>A budget together with how much has been spent (and reserved) in its current period.</summary>
public sealed record BudgetState(Budget Budget, PeriodWindow Window, decimal SpentUsd);

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
            .Where(s => s.Budget.IsActive && s.SpentUsd >= s.Budget.Limit)
            .OrderBy(s => s.Budget.Scope == BudgetScope.VirtualKey ? 0 : s.Budget.Scope == BudgetScope.Team ? 1 : 2)
            .FirstOrDefault();
        return exceeded is null ? BudgetDecision.Allow : new BudgetDecision(false, exceeded);
    }

    /// <summary>
    /// Returns the alert thresholds crossed when use moved from <paramref name="before"/> to <paramref name="after"/>, in
    /// the budget's unit (USD or tokens).
    /// </summary>
    public static IReadOnlyList<int> CrossedThresholds(Budget budget, decimal before, decimal after)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.Limit <= 0 || after <= before)
        {
            return [];
        }

        return budget.AlertThresholds
            .Where(t => t is > 0 and <= 1000)
            .Distinct()
            .Order()
            .Where(t =>
            {
                var level = budget.Limit * t / 100m;
                return before < level && after >= level;
            })
            .ToList();
    }

    public static decimal PercentUsed(decimal spent, decimal limit) =>
        limit <= 0 ? 0 : Math.Round(spent / limit * 100m, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How full a counter is, as a percentage clamped to 0-100 (not rounded). A limit of zero or less is always
    /// full, matching enforcement, which rejects when spend has reached the limit.
    /// </summary>
    public static double UtilisationPercent(decimal spent, decimal limit) =>
        limit <= 0 ? 100 : (double)Math.Clamp(spent / limit * 100m, 0m, 100m);
}
