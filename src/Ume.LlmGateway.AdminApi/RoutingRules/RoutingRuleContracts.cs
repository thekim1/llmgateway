using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;

namespace Ume.LlmGateway.AdminApi.RoutingRules;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

/// <summary>Limits of routing rules, shared by the API and the configuration document.</summary>
public static class RoutingRuleLimits
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxPriority = 1_000_000;
}

public sealed record RuleTargetRequest(
    [property: Required, StringLength(RoutingRuleLimits.MaxNameLength)] string Model,
    [property: Range(1, RoutingRuleSet.MaxWeight)] int Weight = 1) : AdminRequest;

public sealed record RoutingRuleRequest(
    [property: Required, StringLength(RoutingRuleLimits.MaxNameLength)] string Name,
    [property: EnumDataType(typeof(RoutingScope))] RoutingScope Scope,
    [property: Required, MinLength(1), MaxLength(RoutingRuleSet.MaxTargets)] RuleTargetRequest[] Targets,
    [property: MaxLength(RoutingRuleSet.MaxOwners)] Guid[]? ScopeIds = null,
    [property: StringLength(RoutingRuleLimits.MaxDescriptionLength)] string? Description = null,
    bool IsEnabled = true,
    [property: Range(-RoutingRuleLimits.MaxPriority, RoutingRuleLimits.MaxPriority)] int Priority = 0,
    [property: StringLength(RoutingExpression.MaxLength)] string? Condition = null,
    bool Chain = false,
    [property: MaxLength(RoutingRuleSet.MaxTargets)] string[]? Fallbacks = null) : AdminRequest;

public sealed record RoutingRuleReassignRequest(
    [property: EnumDataType(typeof(RoutingScope))] RoutingScope Scope,
    [property: MaxLength(RoutingRuleSet.MaxOwners)] Guid[]? ScopeIds = null,
    bool Enable = true) : AdminRequest;

/// <summary>Order within one scope (all key rules, all team rules, ...); rules shared by several owners keep one priority.</summary>
public sealed record RoutingRuleReorderRequest(
    [property: EnumDataType(typeof(RoutingScope))] RoutingScope Scope,
    [property: Required, MinLength(1), MaxLength(1000)] Guid[] RuleIds) : AdminRequest;

/// <summary>The on/off switch of a rule.</summary>
public sealed record RoutingRuleEnabledRequest(bool IsEnabled) : AdminRequest;

public sealed record ConditionCheckRequest([property: StringLength(5000)] string? Condition) : AdminRequest;

public sealed record RoutingRuleTestRequest(
    [property: Required, StringLength(200)] string Model,
    [property: StringLength(50)] string? Endpoint = null,
    Dictionary<string, string>? Headers = null,
    Dictionary<string, JsonElement>? Params = null,
    Guid? KeyId = null,
    Guid? TeamId = null,
    Guid? DepartmentId = null,
    [property: Range(0, 100)] double? BudgetUsed = null,
    [property: Range(0, 100)] double? TokensUsed = null,
    bool? PiiDetected = null,
    [property: Range(0, long.MaxValue)] long? PromptTokens = null,
    int? Seed = null) : AdminRequest;

/// <summary>
/// <c>ScopeIds</c>/<c>Owners</c>: the keys, teams or departments a non-global rule is shared by (an owner's name is null
/// when it no longer exists). <c>IsOrphaned</c>: none of them exists any more. Such a rule never applies (and cannot be
/// enabled) until it is reassigned. <c>IsTemplate</c>: a shipped example, disabled until adopted. <c>MissingModels</c>:
/// targets or fallbacks that do not exist; the rule cannot be enabled until they do. <c>ValidationErrors</c> lists why
/// the gateway ignores an enabled rule.
/// </summary>
public sealed record RuleView(
    Guid Id, string Name, string? Description, bool IsEnabled, bool IsTemplate, int Priority, string Scope, IReadOnlyList<Guid> ScopeIds,
    IReadOnlyList<RuleOwnerView> Owners, bool IsOrphaned, string Condition, bool Chain, IReadOnlyList<RuleTargetView> Targets, IReadOnlyList<string> Fallbacks,
    IReadOnlyList<string> MissingModels, IReadOnlyList<RuleProblem> ValidationErrors, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RuleOwnerView(Guid Id, string? Name);

public sealed record RuleTargetView(string Model, int Weight);

public sealed record RuleProblem(string Message, int? Position, int? Length);

public sealed record ConditionCheckResult(bool Valid, IReadOnlyList<ConditionError> Errors, IReadOnlyList<string> Variables, IReadOnlyList<VariableView> Available);

public sealed record ConditionError(int Position, int Length, string Message)
{
    public static ConditionError Of(ExpressionError e) => new(e.Position, e.Length, e.Message);
}

public sealed record VariableView(string Name, string Type);

public sealed record RuleTestResult(
    bool Matched, string? PrimaryModel, IReadOnlyList<ModelInfoView> Models, IReadOnlyList<AppliedRuleView> Applied, bool ChainLimitReached,
    IReadOnlyList<RuleEvaluationView> Evaluation, IReadOnlyList<IgnoredRuleView> IgnoredRules, string Note);

public sealed record ModelInfoView(string Name, bool Exists, string Type, string? Kind, bool Enabled);

public sealed record AppliedRuleView(Guid RuleId, string Name, string FromModel, string ToModel);

public sealed record RuleEvaluationView(Guid RuleId, string Name, string Scope, int Priority, int ChainStep, string Model, string Outcome, IReadOnlyList<TraceView> Trace)
{
    public static RuleEvaluationView Of(RuleEvaluation e) => new(
        e.RuleId, e.Name, e.Scope.ToString(), e.Priority, e.ChainStep, e.Model, e.Outcome.ToString(),
        [.. e.Trace.Select(t => new TraceView(t.Text, t.LeftValue, t.Result))]);
}

public sealed record TraceView(string Text, string? LeftValue, bool? Result);

public sealed record IgnoredRuleView(Guid RuleId, string RuleName, string Message, int? Position, int? Length)
{
    public static IgnoredRuleView Of(RuleBuildError e) => new(e.RuleId, e.RuleName, e.Message, e.Expression?.Position, e.Expression?.Length);
}
