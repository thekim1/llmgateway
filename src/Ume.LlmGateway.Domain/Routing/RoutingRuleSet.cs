using Ume.LlmGateway.Domain.Routing.Expressions;

namespace Ume.LlmGateway.Domain.Routing;

/// <summary>
/// An immutable, compiled set of routing rules. Rules are checked by scope (key, team, department, global), then by
/// ascending priority; the first match wins. A matching rule with <c>Chain</c> set rewrites the model and all rules
/// are checked again, until nothing matches, a terminal rule matches, the model stops changing, a model repeats, or the
/// chain depth limit is hit. A rule is applied at most once per evaluation. The last matching rule decides the result.
/// Pure and thread-safe: rules only choose <em>where</em> to send a request; key allow-lists, provider allow-lists,
/// residency and PII restrictions are applied to the result afterwards by the caller and are never widened by a rule.
/// </summary>
public sealed class RoutingRuleSet
{
    public const int MaxChainDepth = 8;
    public const int MaxTargets = 20;
    public const int MaxWeight = 1_000_000;

    private readonly CompiledRule[] _rules;

    private readonly HashSet<string> _variables;

    private RoutingRuleSet(CompiledRule[] rules)
    {
        _rules = rules;
        _variables = new HashSet<string>(rules.SelectMany(r => r.Condition.Variables), StringComparer.Ordinal);
    }

    public static RoutingRuleSet Empty { get; } = new([]);

    public int Count => _rules.Length;

    /// <summary>
    /// True when any enabled rule's condition uses the variable. Lets the gateway skip expensive lookups
    /// (budget and token usage) when no rule needs them.
    /// </summary>
    public bool References(string variable) => _variables.Contains(variable);

    /// <summary>
    /// Compiles rule definitions. Invalid rules are reported in the errors and left out; disabled rules are validated
    /// but not evaluated.
    /// </summary>
    public static (RoutingRuleSet Rules, IReadOnlyList<RuleBuildError> Errors) Build(IEnumerable<RoutingRuleDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var errors = new List<RuleBuildError>();
        var rules = new List<CompiledRule>();
        var names = new HashSet<(RoutingScope, Guid?, string)>();

        foreach (var d in definitions)
        {
            var before = errors.Count;
            Validate(d, errors);
            RoutingExpression? condition = null;
            if (errors.Count == before)
            {
                var (expr, exprErrors) = RoutingExpression.Compile(d.Condition, RoutingSchema.Instance);
                condition = expr;
                errors.AddRange(exprErrors.Select(e => new RuleBuildError(d.Id, d.Name, e.Message, e)));
            }

            if (errors.Count == before && !names.Add((d.Scope, d.ScopeId, d.Name.Trim().ToUpperInvariant())))
            {
                errors.Add(new RuleBuildError(d.Id, d.Name, $"A rule named '{d.Name}' already exists in this scope."));
            }

            if (errors.Count == before && d.IsEnabled)
            {
                rules.Add(new CompiledRule(d, condition!));
            }
        }

        var ordered = rules
            .OrderBy(r => (int)r.Definition.Scope)
            .ThenBy(r => r.Definition.Priority)
            .ThenBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Definition.Id)
            .ToArray();
        return (new RoutingRuleSet(ordered), errors);
    }

    /// <summary>Validates a rule definition without compiling it into a set (for the admin API).</summary>
    public static IReadOnlyList<RuleBuildError> Check(RoutingRuleDefinition definition)
    {
        var errors = new List<RuleBuildError>();
        Validate(definition, errors);
        if (errors.Count == 0)
        {
            var (_, exprErrors) = RoutingExpression.Compile(definition.Condition, RoutingSchema.Instance);
            errors.AddRange(exprErrors.Select(e => new RuleBuildError(definition.Id, definition.Name, e.Message, e)));
        }

        return errors;
    }

    /// <summary>Evaluates the rules for a request. <paramref name="explain"/> collects why each rule matched or not.</summary>
    public RoutingDecision Evaluate(RoutingContext context, Random random, List<RuleEvaluation>? explain = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(random);

        var current = context.Model;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
        var appliedIds = new HashSet<Guid>();
        var applied = new List<AppliedRule>();
        IReadOnlyList<string> models = [];
        var limitReached = false;

        for (var step = 0; ; step++)
        {
            if (step >= MaxChainDepth)
            {
                limitReached = true;
                break;
            }

            var variables = context.ToVariables(current);
            var rule = FindMatch(context, variables, current, step, appliedIds, explain);
            if (rule is null)
            {
                break;
            }

            var def = rule.Definition;
            models = OrderModels(def, random);
            appliedIds.Add(def.Id);
            applied.Add(new AppliedRule(def.Id, def.Name, current, models[0]));

            if (!def.Chain || string.Equals(models[0], current, StringComparison.OrdinalIgnoreCase) || !visited.Add(models[0]))
            {
                break;
            }

            current = models[0];
        }

        return new RoutingDecision(models, applied, limitReached);
    }

    private CompiledRule? FindMatch(RoutingContext context, ExpressionVariables variables, string model, int step, HashSet<Guid> appliedIds, List<RuleEvaluation>? explain)
    {
        foreach (var rule in _rules)
        {
            var def = rule.Definition;
            if (!InScope(def, context))
            {
                continue;
            }

            if (appliedIds.Contains(def.Id))
            {
                explain?.Add(new RuleEvaluation(def.Id, def.Name, def.Scope, def.Priority, step, model, RuleOutcome.Skipped, []));
                continue;
            }

            bool matched;
            if (explain is null)
            {
                matched = rule.Condition.Matches(variables);
            }
            else
            {
                var result = rule.Condition.Explain(variables);
                matched = result.Matched;
                explain.Add(new RuleEvaluation(def.Id, def.Name, def.Scope, def.Priority, step, model, matched ? RuleOutcome.Matched : RuleOutcome.NotMatched, result.Trace));
            }

            if (matched)
            {
                return rule;
            }
        }

        return null;
    }

    private static bool InScope(RoutingRuleDefinition def, RoutingContext context) => def.Scope switch
    {
        RoutingScope.VirtualKey => def.ScopeId is { } id && (id == context.KeyId || context.KeyLineage?.Contains(id) == true),
        RoutingScope.Team => def.ScopeId is { } id && id == context.TeamId,
        RoutingScope.Department => def.ScopeId is { } id && id == context.DepartmentId,
        _ => true,
    };

    /// <summary>Targets in weighted-random order (so the rest act as failover), followed by the explicit fallbacks.</summary>
    private static List<string> OrderModels(RoutingRuleDefinition def, Random random)
    {
        var pool = def.Targets.ToList();
        var result = new List<string>(pool.Count + (def.Fallbacks?.Count ?? 0));
        while (pool.Count > 0)
        {
            var pick = random.Next(pool.Sum(t => t.Weight));
            var index = 0;
            for (; index < pool.Count - 1; index++)
            {
                pick -= pool[index].Weight;
                if (pick < 0)
                {
                    break;
                }
            }

            result.Add(pool[index].Model);
            pool.RemoveAt(index);
        }

        result.AddRange(def.Fallbacks ?? []);
        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static void Validate(RoutingRuleDefinition d, List<RuleBuildError> errors)
    {
        void Fail(string message) => errors.Add(new RuleBuildError(d.Id, d.Name, message));

        if (string.IsNullOrWhiteSpace(d.Name))
        {
            Fail("A rule needs a name.");
        }

        if (d.Scope != RoutingScope.Global && d.ScopeId is null)
        {
            Fail($"A {d.Scope} rule needs a scope id.");
        }

        if (d.Scope == RoutingScope.Global && d.ScopeId is not null)
        {
            Fail("A global rule must not have a scope id.");
        }

        if (d.Targets is null || d.Targets.Count == 0)
        {
            Fail("A rule needs at least one target.");
            return;
        }

        if (d.Targets.Count > MaxTargets)
        {
            Fail($"A rule can have at most {MaxTargets} targets.");
        }

        if (d.Targets.Any(t => string.IsNullOrWhiteSpace(t.Model)))
        {
            Fail("Every target needs a model.");
        }

        if (d.Targets.Any(t => t.Weight is < 1 or > MaxWeight))
        {
            Fail($"Target weights must be between 1 and {MaxWeight}.");
        }

        if (d.Fallbacks is not null && d.Fallbacks.Any(string.IsNullOrWhiteSpace))
        {
            Fail("Fallback models must not be empty.");
        }
    }

    private sealed record CompiledRule(RoutingRuleDefinition Definition, RoutingExpression Condition);
}
