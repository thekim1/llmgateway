using Ume.LlmGateway.Domain.Routing.Expressions;

namespace Ume.LlmGateway.Domain.Routing;

/// <summary>Where a rule applies. The numeric order is the evaluation order: the most specific scope is checked first.</summary>
public enum RoutingScope
{
    VirtualKey = 0,
    Team = 1,
    Department = 2,
    Global = 3,
}

/// <summary>A weighted destination: a route alias or a concrete deployment name. Weights are relative (1..1000000).</summary>
public sealed record RuleTarget(string Model, int Weight = 1);

/// <summary>A routing rule as configured by an administrator.</summary>
public sealed record RoutingRuleDefinition(
    Guid Id,
    string Name,
    bool IsEnabled,
    int Priority,
    RoutingScope Scope,
    Guid? ScopeId,
    string Condition,
    bool Chain,
    IReadOnlyList<RuleTarget> Targets,
    IReadOnlyList<string> Fallbacks);

/// <summary>What a rule's condition can see about the request. Null values are left unset, so conditions that use them do not match.</summary>
public sealed record RoutingContext
{
    public required string Model { get; init; }
    public string? Endpoint { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, object?>? Params { get; init; }
    public Guid? KeyId { get; init; }

    /// <summary>
    /// The key and the keys it replaced through rotation. A rule scoped to a key keeps applying to the key that
    /// replaced it, like budgets do. When null only <see cref="KeyId"/> is used.
    /// </summary>
    public IReadOnlySet<Guid>? KeyLineage { get; init; }
    public string? KeyName { get; init; }
    public Guid? TeamId { get; init; }
    public string? TeamName { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }

    /// <summary>Percent (0-100) of the most specific budget used, if a budget applies.</summary>
    public double? BudgetUsed { get; init; }

    /// <summary>Percent (0-100) of the tokens-per-minute limit used, if a limit applies.</summary>
    public double? TokensUsed { get; init; }

    public bool? PiiDetected { get; init; }
    public long? PromptTokens { get; init; }

    internal ExpressionVariables ToVariables(string model) => new ExpressionVariables()
        .Set("model", model)
        .Set("endpoint", Endpoint)
        .Set("headers", Headers ?? new Dictionary<string, string>())
        .SetMap("params", Params ?? new Dictionary<string, object?>())
        .Set("key_id", KeyId?.ToString("D"))
        .Set("key_name", KeyName)
        .Set("team_id", TeamId?.ToString("D"))
        .Set("team_name", TeamName)
        .Set("department_id", DepartmentId?.ToString("D"))
        .Set("department_name", DepartmentName)
        .SetOptional("budget_used", BudgetUsed)
        .SetOptional("tokens_used", TokensUsed)
        .SetOptional("pii_detected", PiiDetected)
        .SetOptional("prompt_tokens", PromptTokens);
}

/// <summary>The variables available to routing conditions.</summary>
public static class RoutingSchema
{
    public static ExpressionSchema Instance { get; } = new(new Dictionary<string, ExprType>
    {
        ["model"] = ExprType.Text,
        ["endpoint"] = ExprType.Text,
        ["headers"] = ExprType.TextMap,
        ["params"] = ExprType.AnyMap,
        ["key_id"] = ExprType.Text,
        ["key_name"] = ExprType.Text,
        ["team_id"] = ExprType.Text,
        ["team_name"] = ExprType.Text,
        ["department_id"] = ExprType.Text,
        ["department_name"] = ExprType.Text,
        ["budget_used"] = ExprType.Number,
        ["tokens_used"] = ExprType.Number,
        ["pii_detected"] = ExprType.Bool,
        ["prompt_tokens"] = ExprType.Number,
    });
}

/// <summary>One rule that took part in a decision. <c>FromModel</c> is what the request asked for at that step.</summary>
public sealed record AppliedRule(Guid RuleId, string Name, string FromModel, string ToModel);

/// <summary>
/// The outcome of evaluating rules. <c>Models</c> is the attempt order: the weighted-ordered targets of the last
/// matching rule, then its fallbacks (duplicates removed). Empty when no rule matched.
/// </summary>
public sealed record RoutingDecision(IReadOnlyList<string> Models, IReadOnlyList<AppliedRule> Applied, bool ChainLimitReached)
{
    public bool Matched => Models.Count > 0;

    public string? PrimaryModel => Models.Count > 0 ? Models[0] : null;
}

public enum RuleOutcome
{
    Matched,
    NotMatched,
    /// <summary>Already applied earlier in this chain, so not evaluated again.</summary>
    Skipped,
}

/// <summary>Why one rule did or did not match at one chain step; produced on request for dry runs.</summary>
public sealed record RuleEvaluation(Guid RuleId, string Name, RoutingScope Scope, int Priority, int ChainStep, string Model, RuleOutcome Outcome, IReadOnlyList<TraceEntry> Trace);

/// <summary>A rule that could not be used, with the reason. Rules with errors are left out of the rule set.</summary>
public sealed record RuleBuildError(Guid RuleId, string RuleName, string Message, ExpressionError? Expression = null);
