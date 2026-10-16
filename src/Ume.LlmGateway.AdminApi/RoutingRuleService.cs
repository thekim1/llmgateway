using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>What happens to a team's or department's routing rules when it is deleted (the <c>routingRules</c> query value).</summary>
public enum ScopedRulesChoice
{
    /// <summary>Delete the rules with it.</summary>
    Delete,

    /// <summary>Keep the rules, disabled, until they are reassigned (they often belong to a service, not to the organisation).</summary>
    Deactivate,
}

/// <summary>Administration of routing rules. See docs/routing-rules.md.</summary>
public sealed class RoutingRuleService(AdminContext ctx, OwnerScopeResolver owners)
{
    public const string ScopedRulesCode = "routing_rules_scoped";

    private const string DryRunNote =
        "Only the usage values you supply (budgetUsed, tokensUsed, piiDetected, ...) are known to the rules; a condition that uses a value you left out does not match.";

    // ---- reads -------------------------------------------------------------------------------------------

    public async Task<List<RuleView>> ListAsync(RoutingScope? scope, Guid? scopeId, bool? orphaned, CancellationToken ct)
    {
        var query = ctx.Db.RoutingRules.Include(r => r.Targets).AsQueryable();
        if (scope is { } s) { query = query.Where(r => r.Scope == s); }
        if (scopeId is { } id) { query = query.Where(r => r.ScopeIds.Contains(id)); }
        var list = (await query.ToListAsync(ct))
            .OrderBy(r => (int)r.Scope).ThenBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
        return [.. (await ViewsAsync(list, ct)).Where(r => orphaned is not { } o || r.IsOrphaned == o)];
    }

    public async Task<RuleView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync(await FindAsync(id, ct), ct);

    // ---- writes ------------------------------------------------------------------------------------------

    public async Task<RuleView> CreateAsync(RoutingRuleRequest input, CancellationToken ct)
    {
        var rule = new RoutingRule { Name = input.Name.Trim(), CreatedAt = ctx.Now, UpdatedAt = ctx.Now };
        await ApplyRequestAsync(rule, input, isNew: true, ct);
        ctx.Db.RoutingRules.Add(rule);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.RoutingRule, rule.Id, null, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    public async Task<RuleView> UpdateAsync(Guid id, RoutingRuleRequest input, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        var before = RoutingRuleAudit.Of(rule);
        await ApplyRequestAsync(rule, input, isNew: false, ct);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.RoutingRule, id, before, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        ctx.Db.RoutingRules.Remove(rule);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.RoutingRule, id, RoutingRuleAudit.Of(rule), null, InvalidationKind.Config, ct);
    }

    /// <summary>
    /// Attaches a rule (typically one deactivated when its last team or department was removed) to new owners. Every owner
    /// must exist; enabling also requires the rule's models to exist.
    /// </summary>
    public async Task<RuleView> ReassignAsync(Guid id, RoutingRuleReassignRequest input, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        var ids = RuleInput.Owners(input.ScopeIds);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await ValidateScopeAsync(input.Scope, ids, unchanged: [], requireOwner: true, errors, ct);
        if (input.Enable)
        {
            AddModelProblems(RuleInput.Of(rule) with { Scope = input.Scope, ScopeIds = ids, IsEnabled = true }, rule.Id, await KnownModelNamesAsync(ct), errors);
        }

        if (errors.Count > 0) { throw ApiFaultException.Validation(errors); }
        await EnsureNameFreeAsync(rule.Name, input.Scope, ids, rule.Id, ct);

        var before = RoutingRuleAudit.Of(rule);
        rule.Scope = input.Scope;
        rule.ScopeIds = [.. ids];
        rule.IsEnabled = input.Enable;
        rule.IsTemplate = false;
        rule.UpdatedAt = ctx.Now;
        await ctx.SaveAsync(AuditActions.Reassign, AuditEntities.RoutingRule, id, before, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    /// <summary>
    /// The on/off switch. Enabling checks the stored rule as a save would (owners and models must exist), so a template
    /// whose models are not set up yet stays off with a clear message; an enabled template becomes an ordinary rule.
    /// </summary>
    public async Task<RuleView> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        if (rule.IsEnabled == enabled)
        {
            return await ViewAsync(rule, ct);
        }

        if (enabled)
        {
            var input = RuleInput.Of(rule) with { IsEnabled = true };
            var errors = await ProblemsAsync(rule.Id, input, unchanged: [], ct);
            if (errors.Count > 0)
            {
                throw new ApiFaultException(400, $"The rule cannot be activated. {string.Join(" ", errors.Values.SelectMany(m => m))}",
                    new Dictionary<string, object?> { ["errors"] = errors });
            }
        }

        var before = RoutingRuleAudit.Of(rule);
        rule.IsEnabled = enabled;
        rule.IsTemplate &= !enabled;
        rule.UpdatedAt = ctx.Now;
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.RoutingRule, id, before, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    /// <summary>
    /// Sets the evaluation order within one scope (all key rules, all team rules, ...): the listed rules get priorities
    /// 0, 10, 20, ... Rules shared by several owners keep the same relative order for each of them.
    /// </summary>
    public async Task<List<RuleView>> ReorderAsync(RoutingRuleReorderRequest input, CancellationToken ct)
    {
        var ids = input.RuleIds.Distinct().ToArray();
        var list = await ctx.Db.RoutingRules.Include(r => r.Targets)
            .Where(r => ids.Contains(r.Id) && r.Scope == input.Scope).ToListAsync(ct);
        if (list.Count != ids.Length || ids.Length != input.RuleIds.Length)
        {
            throw new ApiFaultException(400, "All rules must exist and belong to the same scope, and each may be given only once.");
        }

        var before = list.OrderBy(r => r.Priority).Select(r => new RulePriorityAudit(r.Id, r.Priority)).ToList();
        for (var i = 0; i < ids.Length; i++)
        {
            var rule = list.Single(r => r.Id == ids[i]);
            rule.Priority = i * 10;
            rule.UpdatedAt = ctx.Now;
        }

        await ctx.SaveAsync(AuditActions.Reorder, AuditEntities.RoutingRule, input.Scope.ToString(), before,
            ids.Select((id, i) => new RulePriorityAudit(id, i * 10)).ToList(), InvalidationKind.Config, ct);
        return [.. (await ViewsAsync(list, ct)).OrderBy(r => r.Priority)];
    }

    // ---- condition check and dry run ---------------------------------------------------------------------

    /// <summary>Live validation of a condition while it is being typed.</summary>
    public static ConditionCheckResult CheckCondition(string? condition)
    {
        var (expression, errors) = RoutingExpression.Compile(condition, RoutingSchema.Instance);
        return new ConditionCheckResult(
            errors.Count == 0,
            [.. errors.Select(ConditionError.Of)],
            expression?.Variables.Order(StringComparer.Ordinal).ToArray() ?? [],
            [.. RoutingSchema.Instance.Variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new VariableView(v.Key, v.Value.ToString()))]);
    }

    /// <summary>Dry run: what would the stored, enabled rules do with this request, and why?</summary>
    public async Task<RuleTestResult> TestAsync(RoutingRuleTestRequest input, CancellationToken ct)
    {
        var stored = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).ToListAsync(ct);
        var (set, buildErrors) = RoutingRuleSet.Build(stored.Select(r => r.ToDefinition()));
        var context = await TestContextAsync(input, ct);

        var explain = new List<RuleEvaluation>();
        var random = input.Seed is { } seed ? new Random(seed) : Random.Shared;
        var decision = set.Evaluate(context, random, explain);

        var aliases = await ctx.Db.RouteAliases.AsNoTracking().Select(a => new { a.Name, a.Kind, a.IsEnabled }).ToListAsync(ct);
        var deployments = await DeploymentNamesAsync(ct);
        ModelInfoView ModelInfo(string name) =>
            aliases.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a ? new ModelInfoView(name, true, "alias", a.Kind.ToString(), a.IsEnabled)
            : deployments.FirstOrDefault(d => d.Names.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } d ? new ModelInfoView(name, true, "deployment", d.Kind.ToString(), d.IsEnabled)
            : new ModelInfoView(name, false, "unknown", null, false);

        return new RuleTestResult(
            decision.Matched, decision.PrimaryModel,
            [.. decision.Models.Select(ModelInfo)],
            [.. decision.Applied.Select(a => new AppliedRuleView(a.RuleId, a.Name, a.FromModel, a.ToModel))],
            decision.ChainLimitReached,
            [.. explain.Select(RuleEvaluationView.Of)],
            [.. buildErrors.Select(IgnoredRuleView.Of)],
            DryRunNote);
    }

    // ---- scope removal: ask the user what to do with the rules ------------------------------------------

    /// <summary>
    /// Called when a team or department is about to be deleted. It is removed from every rule shared with other owners
    /// (those rules keep applying to the rest). If it is the only owner of some rules the caller must say what to do with
    /// them (<see cref="ScopedRulesChoice"/>); without a choice the delete fails with 409 and a machine-readable list of
    /// those rules and the choices. The changes are staged and saved with the delete.
    /// </summary>
    public async Task ApplyScopeRemovalAsync(RoutingScope scope, Guid scopeId, string? choice, string what, CancellationToken ct)
    {
        var rules = await ctx.Db.RoutingRules.Include(r => r.Targets).Where(r => r.Scope == scope && r.ScopeIds.Contains(scopeId)).ToListAsync(ct);
        if (rules.Count == 0)
        {
            return;
        }

        var shared = rules.Where(r => r.ScopeIds.Any(id => id != scopeId)).ToList();
        var sole = rules.Except(shared).ToList();
        ScopedRulesChoice parsed = default;
        if (sole.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(choice))
            {
                throw new ApiFaultException(409,
                    $"{what} is the only owner of {sole.Count} routing rule(s). Choose whether the rules are deleted or deactivated until they are linked to a new owner.",
                    new Dictionary<string, object?>
                    {
                        ["code"] = ScopedRulesCode,
                        ["choices"] = EnumQuery.Names<ScopedRulesChoice>(),
                        ["rules"] = sole.OrderBy(r => r.Priority).Select(r => new { r.Id, r.Name, r.IsEnabled }).ToList(),
                    });
            }

            if (!EnumQuery.TryParse(choice, out parsed))
            {
                throw new ApiFaultException(400, $"routingRules must be {string.Join(" or ", EnumQuery.Names<ScopedRulesChoice>().Select(c => $"'{c}'"))}.");
            }
        }

        foreach (var rule in shared)
        {
            var before = RoutingRuleAudit.Of(rule);
            rule.ScopeIds = [.. rule.ScopeIds.Where(id => id != scopeId)];
            rule.UpdatedAt = ctx.Now;
            ctx.StageAudit(AuditActions.Update, AuditEntities.RoutingRule, rule.Id, before,
                new RuleScopeRemovalAudit($"{what} {scope} {scopeId} was deleted; the rule still applies to its other owners", ScopeIds: [.. rule.ScopeIds]));
        }

        foreach (var rule in sole)
        {
            var before = RoutingRuleAudit.Of(rule);
            if (parsed == ScopedRulesChoice.Delete)
            {
                ctx.Db.RoutingRules.Remove(rule);
                ctx.StageAudit(AuditActions.Delete, AuditEntities.RoutingRule, rule.Id, before, new RuleScopeRemovalAudit($"{what} {scope} {scopeId} was deleted"));
            }
            else
            {
                rule.IsEnabled = false;
                rule.ScopeIds = [];
                rule.UpdatedAt = ctx.Now;
                ctx.StageAudit(AuditActions.Deactivate, AuditEntities.RoutingRule, rule.Id, before,
                    new RuleScopeRemovalAudit($"{what} {scope} {scopeId} was deleted; waiting to be linked to a new owner", false, []));
            }
        }
    }

    // ---- integrity: names used by rules cannot disappear -------------------------------------------------

    /// <summary>Throws 409 if a rule uses <paramref name="name"/> as target or fallback (a route alias or model being removed or renamed).</summary>
    public async Task EnsureNotUsedByRulesAsync(string name, string action, CancellationToken ct)
    {
        var rules = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).ToListAsync(ct);
        var user = rules.FirstOrDefault(r =>
            r.Targets.Any(t => string.Equals(t.Model, name, StringComparison.OrdinalIgnoreCase)) ||
            r.Fallbacks.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)));
        if (user is not null)
        {
            throw new ApiFaultException(409, $"'{name}' is used by the routing rule '{user.Name}' and cannot be {action}. Change the rule first.");
        }
    }

    // ---- import ------------------------------------------------------------------------------------------

    /// <summary>
    /// Imports global rules by name (create or update; rules not in the document are left alone, as with routes).
    /// Every rule is validated like one saved through the API; an invalid rule fails the whole import.
    /// <paramref name="importedNames"/> are routes and models created by the same import.
    /// </summary>
    public async Task<(int Created, int Updated)> ImportGlobalAsync(IReadOnlyCollection<ConfigRoutingRule> items, IReadOnlyCollection<string> importedNames, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return (0, 0);
        }

        var known = await KnownModelNamesAsync(ct);
        known.UnionWith(importedNames);
        var existing = await ctx.Db.RoutingRules.Include(r => r.Targets).Where(r => r.Scope == RoutingScope.Global).ToListAsync(ct);
        int created = 0, updated = 0;
        foreach (var item in items)
        {
            var input = RuleInput.From(item);
            var rule = existing.FirstOrDefault(r => string.Equals(r.Name, input.Name, StringComparison.OrdinalIgnoreCase));
            var problems = Check(rule?.Id ?? Guid.Empty, input, known);
            var problem = problems.Definition.Select(e => e.Message).FirstOrDefault()
                ?? (input.IsEnabled ? problems.UnknownTargets.Select(t => $"unknown model or alias '{t}'").FirstOrDefault() : null)
                ?? (input.IsEnabled ? problems.UnknownFallbacks.Select(f => $"unknown fallback model or alias '{f}'").FirstOrDefault() : null);
            if (problem is not null)
            {
                throw new ApiFaultException(400, $"The routing rule '{input.Name}' is invalid: {problem.TrimEnd('.')}.");
            }

            if (rule is null)
            {
                rule = new RoutingRule { Name = input.Name, CreatedAt = ctx.Now };
                ctx.Db.RoutingRules.Add(rule);
                created++;
            }
            else
            {
                updated++;
            }

            Write(rule, input);
        }

        return (created, updated);
    }

    // ---- internals ---------------------------------------------------------------------------------------

    /// <summary>
    /// A rule as written by the API or an import: names trimmed, empty and duplicate fallbacks and owners dropped. The
    /// condition is checked as typed (error positions refer to the text the user sees) and stored trimmed.
    /// </summary>
    private sealed record RuleInput(
        string Name, string? Description, bool IsEnabled, int Priority, RoutingScope Scope, IReadOnlyList<Guid> ScopeIds, string Condition, bool Chain,
        IReadOnlyList<RuleTarget> Targets, IReadOnlyList<string> Fallbacks)
    {
        public static RuleInput From(RoutingRuleRequest r) =>
            Create(r.Name, r.Description, r.IsEnabled, r.Priority, r.Scope, r.ScopeIds, r.Condition, r.Chain, r.Targets, r.Fallbacks);

        /// <summary>Imported rules are always global: scoped rules' ids differ between environments.</summary>
        public static RuleInput From(ConfigRoutingRule r) =>
            Create(r.Name, r.Description, r.IsEnabled, r.Priority, RoutingScope.Global, null, r.Condition, r.Chain, r.Targets, r.Fallbacks);

        /// <summary>A stored rule, for checks that do not come with a request (the on/off switch, reassigning).</summary>
        public static RuleInput Of(RoutingRule r) => new(
            r.Name, r.Description, r.IsEnabled, r.Priority, r.Scope, [.. r.ScopeIds], r.Condition, r.Chain,
            [.. r.Targets.Select(t => new RuleTarget(t.Model, t.Weight))], [.. r.Fallbacks]);

        /// <summary>The owners as stored: distinct, without empty ids (a global rule sent with owners is rejected by <see cref="ValidateScopeAsync"/>).</summary>
        public static IReadOnlyList<Guid> Owners(IEnumerable<Guid>? ids) => [.. (ids ?? []).Where(id => id != Guid.Empty).Distinct()];

        private static RuleInput Create(string name, string? description, bool isEnabled, int priority, RoutingScope scope, IEnumerable<Guid>? scopeIds,
            string? condition, bool chain, IEnumerable<RuleTargetRequest> targets, IEnumerable<string>? fallbacks) => new(
            name.Trim(), description, isEnabled, priority, scope, Owners(scopeIds), condition ?? "", chain,
            [.. targets.Select(t => new RuleTarget(t.Model.Trim(), t.Weight))],
            [.. (fallbacks ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);

        public RoutingRuleDefinition Definition(Guid id) => new(id, Name, IsEnabled, Priority, Scope, ScopeIds, Condition, Chain, Targets, Fallbacks);
    }

    /// <summary>Why a rule cannot be saved: the domain's own checks, and names that are neither a route alias nor a model.</summary>
    private sealed record RuleProblems(IReadOnlyList<RuleBuildError> Definition, IReadOnlyList<string> UnknownTargets, IReadOnlyList<string> UnknownFallbacks);

    /// <summary>Only chained rules may target names that another rule handles; fallbacks must always exist.</summary>
    private static RuleProblems Check(Guid id, RuleInput input, HashSet<string> known) => new(
        RoutingRuleSet.Check(input.Definition(id)),
        input.Chain ? [] : [.. input.Targets.Where(t => !known.Contains(t.Model)).Select(t => t.Model)],
        [.. input.Fallbacks.Where(f => !known.Contains(f))]);

    /// <summary>Writes a checked rule, replacing its targets.</summary>
    private void Write(RoutingRule rule, RuleInput input)
    {
        rule.Name = input.Name;
        rule.Description = input.Description;
        rule.IsEnabled = input.IsEnabled;
        rule.Priority = input.Priority;
        rule.Scope = input.Scope;
        rule.ScopeIds = [.. input.ScopeIds];
        rule.IsTemplate = false;
        rule.Condition = input.Condition.Trim();
        rule.Chain = input.Chain;
        rule.Fallbacks = [.. input.Fallbacks];
        rule.UpdatedAt = ctx.Now;
        ctx.Db.RoutingRuleTargets.RemoveRange(rule.Targets);
        rule.Targets = [.. input.Targets.Select(t => new RoutingRuleTarget { RoutingRuleId = rule.Id, Model = t.Model, Weight = t.Weight })];
        ctx.Db.RoutingRuleTargets.AddRange(rule.Targets);
    }

    private async Task<RoutingRule> FindAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.RoutingRules.Include(r => r.Targets).SingleOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new ApiFaultException(404, "The rule does not exist.");

    /// <summary>Validates the request (field errors are a validation fault) and writes it onto the rule.</summary>
    private async Task ApplyRequestAsync(RoutingRule rule, RoutingRuleRequest request, bool isNew, CancellationToken ct)
    {
        var input = RuleInput.From(request);
        // Owners the rule already had keep it valid while it is disabled, even if one of them is gone; new ones must exist.
        IReadOnlyCollection<Guid> unchanged = isNew || rule.Scope != input.Scope ? [] : rule.ScopeIds;
        var errors = await ProblemsAsync(rule.Id, input, unchanged, ct);
        if (errors.Count > 0)
        {
            throw ApiFaultException.Validation(errors);
        }

        await EnsureNameFreeAsync(input.Name, input.Scope, input.ScopeIds, rule.Id, ct);
        Write(rule, input);
    }

    /// <summary>Everything that stops a rule from being saved, by field. Missing models only matter for an enabled rule.</summary>
    private async Task<Dictionary<string, string[]>> ProblemsAsync(Guid id, RuleInput input, IReadOnlyCollection<Guid> unchanged, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await ValidateScopeAsync(input.Scope, input.ScopeIds, input.IsEnabled ? [] : unchanged, requireOwner: input.IsEnabled, errors, ct);
        var problems = Check(id, input, await KnownModelNamesAsync(ct));
        foreach (var error in problems.Definition)
        {
            // Owner problems are reported by ValidateScopeAsync, in Swedish.
            if (error.Expression is null && error.Message.Contains("owner", StringComparison.Ordinal))
            {
                continue;
            }

            var key = error.Expression is not null ? "condition" : "request";
            var text = error.Expression is { } e ? $"{e.Message} (tecken {e.Position + 1})" : error.Message;
            errors[key] = [.. errors.GetValueOrDefault(key, []), text];
        }

        AddModelProblems(problems, input.IsEnabled, errors);
        return errors;
    }

    private static void AddModelProblems(RuleInput input, Guid id, HashSet<string> known, Dictionary<string, string[]> errors) =>
        AddModelProblems(Check(id, input, known), input.IsEnabled, errors);

    /// <summary>
    /// A disabled rule may name models that do not exist yet (a template waiting for its route alias); enabling it is
    /// blocked until they do.
    /// </summary>
    private static void AddModelProblems(RuleProblems problems, bool enabled, Dictionary<string, string[]> errors)
    {
        if (!enabled)
        {
            return;
        }

        if (problems.UnknownTargets.Count > 0)
        {
            errors["targets"] = [$"Unknown model or alias: {string.Join(", ", problems.UnknownTargets)}. Create the alias or change the target before the rule is activated " +
                "(only chained rules may point to names that another rule handles)."];
        }

        if (problems.UnknownFallbacks.Count > 0)
        {
            errors["fallbacks"] = [$"Unknown fallback model or alias: {string.Join(", ", problems.UnknownFallbacks)}. Create the alias or change the fallback before the rule is activated."];
        }
    }

    /// <summary>
    /// Owners must be of the rule's kind and exist. <paramref name="unchanged"/> owners are not checked again (a disabled
    /// rule may keep an owner that has since been removed); an enabled rule needs at least one owner, all existing.
    /// </summary>
    private async Task ValidateScopeAsync(RoutingScope scope, IReadOnlyList<Guid> scopeIds, IReadOnlyCollection<Guid> unchanged, bool requireOwner,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (scope == RoutingScope.Global)
        {
            if (scopeIds.Count > 0)
            {
                errors["scopeIds"] = ["A global rule must not have owners."];
            }

            return;
        }

        if (scopeIds.Count == 0)
        {
            if (requireOwner)
            {
                errors["scopeIds"] = ["Choose at least one key, team or department the rule applies to."];
            }

            return;
        }

        if (scopeIds.Count > RoutingRuleSet.MaxOwners)
        {
            errors["scopeIds"] = [$"A rule can apply to at most {RoutingRuleSet.MaxOwners} owners."];
            return;
        }

        var check = scopeIds.Except(unchanged).Select(id => Owner.Of(scope, id)).OfType<Owner>().ToList();
        if (check.Count == 0)
        {
            return;
        }

        var found = await owners.AllNamesAsync(check, ct);
        var missing = check.Where(o => !found.ContainsKey(o)).Select(o => o.Id).ToList();
        if (missing.Count > 0)
        {
            var label = OwnerScopeResolver.Label(check[0].Kind);
            errors["scopeIds"] = [$"{label} does not exist: {string.Join(", ", missing)}. Link the rule to existing owners before it is activated."];
        }
    }

    /// <summary>A name is unique among global rules, and among the rules of a scope that share an owner.</summary>
    private async Task EnsureNameFreeAsync(string name, RoutingScope scope, IReadOnlyList<Guid> scopeIds, Guid self, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
#pragma warning disable CA1862, CA1304, CA1311 // the comparison must be translatable to SQL (lower() in the database)
        var sameName = await ctx.Db.RoutingRules.AsNoTracking()
            .Where(r => r.Id != self && r.Scope == scope && r.Name.ToLower() == lower).Select(r => r.ScopeIds).ToListAsync(ct);
#pragma warning restore CA1862, CA1304, CA1311
        if (scope == RoutingScope.Global ? sameName.Count > 0 : sameName.Any(other => other.Intersect(scopeIds).Any()))
        {
            throw new ApiFaultException(409, scope == RoutingScope.Global
                ? "There is already a global rule with the same name."
                : "There is already a rule with the same name for one of the rule's owners.");
        }
    }

    private async Task<HashSet<string>> KnownModelNamesAsync(CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(await ctx.Db.RouteAliases.AsNoTracking().Select(a => a.Name).ToListAsync(ct));
        names.UnionWith((await DeploymentNamesAsync(ct)).SelectMany(d => d.Names));
        return names;
    }

    /// <summary>
    /// The names a rule can use for each model: its public name and provider/name. A plain name several providers share is
    /// not one of them; the gateway refuses it as ambiguous (<see cref="PublicModelNames"/>).
    /// </summary>
    private async Task<List<(string[] Names, ModelKind Kind, bool IsEnabled)>> DeploymentNamesAsync(CancellationToken ct)
    {
        var all = await ctx.Db.ModelDeployments.AsNoTracking().Include(d => d.ProviderAccount).ToListAsync(ct);
        var names = PublicModelNames.Public(all);
        return [.. all.Select(d => (new[] { names[d.Id], PublicModelNames.Qualified(d.ProviderAccount!.Name, d.Name) }, d.Kind, d.IsEnabled))];
    }

    private async Task<RuleView> ViewAsync(RoutingRule rule, CancellationToken ct) => (await ViewsAsync([rule], ct))[0];

    private async Task<List<RuleView>> ViewsAsync(IReadOnlyCollection<RoutingRule> rules, CancellationToken ct)
    {
        var names = await owners.AllNamesAsync(rules.SelectMany(r => r.ScopeIds.Select(id => Owner.Of(r.Scope, id))).OfType<Owner>(), ct);
        var known = await KnownModelNamesAsync(ct);
        return [.. rules.Select(r => View(r, names, known))];
    }

    private static RuleView View(RoutingRule r, Dictionary<Owner, string> names, HashSet<string> known)
    {
        var ownerViews = r.ScopeIds
            .Select(id => new RuleOwnerView(id, Owner.Of(r.Scope, id) is { } owner && names.TryGetValue(owner, out var n) ? n : null))
            .OrderBy(o => o.Name is null).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var orphaned = r.Scope != RoutingScope.Global && !ownerViews.Any(o => o.Name is not null);
        var models = Check(r.Id, RuleInput.Of(r), known);
        return new RuleView(
            r.Id, r.Name, r.Description, r.IsEnabled, r.IsTemplate, r.Priority, r.Scope.ToString(), [.. r.ScopeIds], ownerViews, orphaned, r.Condition, r.Chain,
            [.. r.Targets.OrderByDescending(t => t.Weight).ThenBy(t => t.Model, StringComparer.OrdinalIgnoreCase).Select(t => new RuleTargetView(t.Model, t.Weight))],
            r.Fallbacks,
            [.. models.UnknownTargets.Concat(models.UnknownFallbacks).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. RoutingRuleSet.Check(r.ToDefinition()).Select(e => new RuleProblem(e.Message, e.Expression?.Position, e.Expression?.Length))],
            r.CreatedAt, r.UpdatedAt);
    }

    private async Task<RoutingContext> TestContextAsync(RoutingRuleTestRequest input, CancellationToken ct)
    {
        Guid? teamId = input.TeamId, departmentId = input.DepartmentId, keyId = input.KeyId;
        string? keyName = null, teamName = null, departmentName = null;
        IReadOnlySet<Guid>? lineage = null;

        if (keyId is { } kid)
        {
            var key = await ctx.Db.VirtualKeys.AsNoTracking().Include(k => k.Team!).ThenInclude(t => t.Department).SingleOrDefaultAsync(k => k.Id == kid, ct)
                ?? throw new ApiFaultException(404, "The key does not exist.");
            keyName = key.Name;
            teamId ??= key.TeamId;
            departmentId ??= key.Team?.DepartmentId;
            lineage = KeyRotation.Ancestors(kid, await ctx.Db.KeyRotationMapAsync(ct));
        }

        if (teamId is { } tid)
        {
            var team = await ctx.Db.Teams.AsNoTracking().Include(t => t.Department).SingleOrDefaultAsync(t => t.Id == tid, ct);
            teamName = team?.Name;
            departmentId ??= team?.DepartmentId;
        }

        if (departmentId is { } did)
        {
            departmentName = await ctx.Db.Departments.AsNoTracking().Where(d => d.Id == did).Select(d => d.Name).FirstOrDefaultAsync(ct);
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in input.Params ?? [])
        {
            parameters[name] = value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String => value.GetString(),
                _ => null,
            };
        }

        return new RoutingContext
        {
            Model = input.Model,
            Endpoint = input.Endpoint,
            Headers = input.Headers is null ? null : new Dictionary<string, string>(input.Headers, StringComparer.OrdinalIgnoreCase),
            Params = parameters,
            KeyId = keyId,
            KeyLineage = lineage,
            KeyName = keyName,
            TeamId = teamId,
            TeamName = teamName,
            DepartmentId = departmentId,
            DepartmentName = departmentName,
            BudgetUsed = input.BudgetUsed,
            TokensUsed = input.TokensUsed,
            PiiDetected = input.PiiDetected,
            PromptTokens = input.PromptTokens,
        };
    }
}
