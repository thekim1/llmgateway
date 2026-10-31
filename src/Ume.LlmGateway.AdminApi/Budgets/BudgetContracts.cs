using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Budgets;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record BudgetRequest(
    [property: EnumDataType(typeof(BudgetScope))] BudgetScope Scope,
    Guid ScopeId,
    [property: Range(typeof(decimal), "0", "1000000000")] decimal LimitUsd,
    [property: EnumDataType(typeof(BudgetPeriod))] BudgetPeriod Period,
    [property: Required, MaxLength(100)] int[] AlertThresholds,
    bool IsActive = true,
    // Tokens: a quota of input plus output tokens (LimitTokens), optionally counting only one residency. Omitted = Usd.
    [property: EnumDataType(typeof(BudgetUnit))] BudgetUnit Unit = BudgetUnit.Usd,
    [property: Range(0, 1_000_000_000_000)] long LimitTokens = 0,
    [property: EnumDataType(typeof(DataResidency))] DataResidency? Residency = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AlertThresholds?.Any(t => t is < 1 or > 100) == true)
        {
            yield return new ValidationResult("Alert thresholds must be 1–100 percent.", [nameof(AlertThresholds)]);
        }

        if (Unit == BudgetUnit.Tokens && LimitTokens <= 0)
        {
            yield return new ValidationResult("Enter the number of tokens the quota allows.", [nameof(LimitTokens)]);
        }

        if (Unit == BudgetUnit.Usd && Residency is not null)
        {
            yield return new ValidationResult("Only token quotas can count one residency.", [nameof(Residency)]);
        }
    }
}

/// <summary>A budget: cost budgets have <c>LimitUsd</c>/<c>SpentUsd</c>, token quotas <c>LimitTokens</c>/<c>SpentTokens</c> (their USD fields are 0).</summary>
public sealed record BudgetDto(
    Guid Id, BudgetScope Scope, Guid ScopeId, string ScopeName, decimal LimitUsd, BudgetPeriod Period, IReadOnlyList<int> AlertThresholds,
    bool IsActive, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, decimal SpentUsd, decimal PercentUsed,
    BudgetUnit Unit = BudgetUnit.Usd, long LimitTokens = 0, long SpentTokens = 0, DataResidency? Residency = null);

/// <summary>An alert; for a token quota <c>SpentTokens</c>/<c>LimitTokens</c> are set and the USD amounts are 0.</summary>
public sealed record AlertDto(
    Guid Id, Guid BudgetId, BudgetScope Scope, string ScopeName, int ThresholdPercent, decimal SpentUsd, decimal LimitUsd,
    DateTimeOffset PeriodStart, DateTimeOffset Timestamp, bool Acknowledged, long? SpentTokens = null, long? LimitTokens = null);
