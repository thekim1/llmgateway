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

    /// <summary>How many keys, teams or departments one rule can be shared by.</summary>
    public const int MaxOwners = 1000;

    private readonly CompiledRule[] _rules;

    // Enabled rules indexed by owner at build time, each array in evaluation order (priority, name, id), so a request
    // only looks at the rules that can apply to it instead of scanning every rule.
    private readonly Dictionary<Guid, CompiledRule[]> _keyRules;
    private readonly Dictionary<Guid, CompiledRule[]> _teamRules;
    private readonly Dictionary<Guid, CompiledRule[]> _departmentRules;
    private readonly CompiledRule[] _globalRules;

    private readonly HashSet<string> _variables;

    private RoutingRuleSet(CompiledRule[] rules)
    {
        _rules = rules;
        _keyRules = Index(rules, RoutingScope.VirtualKey);
        _teamRules = Index(rules, RoutingScope.Team);
        _departmentRules = Index(rules, RoutingScope.Department);
        _globalRules = [.. rules.Where(r => r.Definition.Scope == RoutingScope.Global)];
        _variables = new HashSet<string>(rules.SelectMany(r => r.Condition.Variables), StringComparer.Ordinal);
    }

    private static Dictionary<Guid, CompiledRule[]> Index(CompiledRule[] ordered, RoutingScope scope) => ordered
        .Where(r => r.Definition.Scope == scope)
        .SelectMany(r => r.Definition.ScopeIds.Distinct().Select(id => (Id: id, Rule: r)))
        .GroupBy(x => x.Id)
        .ToDictionary(g => g.Key, g => g.Select(x => x.Rule).ToArray());

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
        // A name is unique among the rules of a scope that share an owner (or among global rules).
        var names = new HashSet<(RoutingScope, Guid, string)>();

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

            if (errors.Count == before && !AddNames(names, d))
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
            .Select((r, i) => r with { Order = i })
            .ToArray();
        return (new RoutingRuleSet(ordered), errors);
    }

    private static bool AddNames(HashSet<(RoutingScope, Guid, string)> names, RoutingRuleDefinition d)
    {
        var name = d.Name.Trim().ToUpperInvariant();
        if (d.Scope == RoutingScope.Global)
        {
            return names.Add((d.Scope, Guid.Empty, name));
        }

        var free = true;
        foreach (var id in d.ScopeIds.Distinct())
        {
            free &= names.Add((d.Scope, id, name));
        }

        return free;
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

        var keyRules = KeyRules(context);
        var current = context.Model;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
        var appliedIds = new HashSet<Guid>();
        var applied = new List<AppliedRule>();
        List<string> models = [];
        var limitReached = false;

        for (var step = 0; ; step++)
        {
            if (step >= MaxChainDepth)
            {
                limitReached = true;
                break;
            }

            var variables = context.ToVariables(current);
            var rule = FindMatch(keyRules, context, variables, current, step, appliedIds, explain);
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

    /// <summary>Checks the rules that can apply to the request, most specific scope first: key, team, department, global.</summary>
    private CompiledRule? FindMatch(CompiledRule[] keyRules, RoutingContext context, ExpressionVariables variables, string model, int step, HashSet<Guid> appliedIds,
        List<RuleEvaluation>? explain) =>
        Scan(keyRules, variables, model, step, appliedIds, explain)
        ?? Scan(Lookup(_teamRules, context.TeamId), variables, model, step, appliedIds, explain)
        ?? Scan(Lookup(_departmentRules, context.DepartmentId), variables, model, step, appliedIds, explain)
        ?? Scan(_globalRules, variables, model, step, appliedIds, explain);

    private static CompiledRule? Scan(CompiledRule[] rules, ExpressionVariables variables, string model, int step, HashSet<Guid> appliedIds, List<RuleEvaluation>? explain)
    {
        foreach (var rule in rules)
        {
            var def = rule.Definition;
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

    private static CompiledRule[] Lookup(Dictionary<Guid, CompiledRule[]> index, Guid? id) =>
        id is { } value && index.TryGetValue(value, out var rules) ? rules : [];

    /// <summary>
    /// The key-scoped rules for the key and the keys it replaced through rotation (a rule keeps applying to the key that
    /// replaced its key, like budgets do). Usually one key: the indexed array is used as is; otherwise the rules are merged
    /// in evaluation order without duplicates.
    /// </summary>
    private CompiledRule[] KeyRules(RoutingContext context)
    {
        if (_keyRules.Count == 0)
        {
            return [];
        }

        if (context.KeyLineage is not { Count: > 0 } lineage)
        {
            return Lookup(_keyRules, context.KeyId);
        }

        CompiledRule[]? single = null;
        List<CompiledRule>? merged = null;
        foreach (var id in lineage)
        {
            if (!_keyRules.TryGetValue(id, out var rules))
            {
                continue;
            }

            if (single is null && merged is null)
            {
                single = rules;
                continue;
            }

            merged ??= [.. single!];
            merged.AddRange(rules);
        }

        if (merged is null)
        {
            return single ?? Lookup(_keyRules, context.KeyId);
        }

        if (context.KeyId is { } keyId && !lineage.Contains(keyId) && _keyRules.TryGetValue(keyId, out var own))
        {
            merged.AddRange(own);
        }

        return [.. merged.DistinctBy(r => r.Definition.Id).OrderBy(r => r.Order)];
    }

    /// <summary>Targets in weighted-random order (so the rest act as failover), followed by the explicit fallbacks.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Load balancing between targets, not a security decision.")]
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

        var owners = d.ScopeIds?.Count ?? 0;
        if (d.Scope != RoutingScope.Global && owners == 0 && d.IsEnabled)
        {
            Fail($"A {d.Scope} rule needs at least one owner.");
        }

        if (d.Scope == RoutingScope.Global && owners > 0)
        {
            Fail("A global rule must not have owners.");
        }

        if (owners > MaxOwners)
        {
            Fail($"A rule can have at most {MaxOwners} owners.");
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

    /// <summary><c>Order</c> is the rule's position in the evaluation order across all scopes.</summary>
    private sealed record CompiledRule(RoutingRuleDefinition Definition, RoutingExpression Condition, int Order = 0);
}
