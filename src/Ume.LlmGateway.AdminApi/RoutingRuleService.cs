using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
        if (scopeId is { } id) { query = query.Where(r => r.ScopeId == id); }
        var list = (await query.ToListAsync(ct))
            .OrderBy(r => (int)r.Scope).ThenBy(r => r.ScopeId).ThenBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return [.. (await ViewsAsync(list, ct)).Where(r => orphaned is not { } o || r.IsOrphaned == o)];
    }

    public async Task<RuleView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync(await FindAsync(id, ct), ct);

    // ---- writes ------------------------------------------------------------------------------------------

    public async Task<RuleView> CreateAsync(ClaimsPrincipal user, RoutingRuleRequest input, CancellationToken ct)
    {
        var rule = new RoutingRule { Name = input.Name.Trim(), CreatedAt = ctx.Now, UpdatedAt = ctx.Now };
        await ApplyRequestAsync(rule, input, isNew: true, ct);
        ctx.Db.RoutingRules.Add(rule);
        await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.RoutingRule, rule.Id, null, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    public async Task<RuleView> UpdateAsync(ClaimsPrincipal user, Guid id, RoutingRuleRequest input, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        var before = RoutingRuleAudit.Of(rule);
        await ApplyRequestAsync(rule, input, isNew: false, ct);
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.RoutingRule, id, before, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    public async Task DeleteAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        ctx.Db.RoutingRules.Remove(rule);
        await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.RoutingRule, id, RoutingRuleAudit.Of(rule), null, InvalidationKind.Config, ct);
    }

    /// <summary>Attaches a rule (typically one deactivated when its team or department was removed) to a new owner.</summary>
    public async Task<RuleView> ReassignAsync(ClaimsPrincipal user, Guid id, RoutingRuleReassignRequest input, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await ValidateScopeAsync(input.Scope, input.ScopeId, mustExist: true, errors, ct);
        if (errors.Count > 0) { throw ApiFaultException.Validation(errors); }
        await EnsureNameFreeAsync(rule.Name, input.Scope, input.ScopeId, rule.Id, ct);

        var before = RoutingRuleAudit.Of(rule);
        rule.Scope = input.Scope;
        rule.ScopeId = input.Scope == RoutingScope.Global ? null : input.ScopeId;
        rule.IsEnabled = input.Enable;
        rule.UpdatedAt = ctx.Now;
        await ctx.SaveAsync(user, AuditActions.Reassign, AuditEntities.RoutingRule, id, before, RoutingRuleAudit.Of(rule), InvalidationKind.Config, ct);
        return await ViewAsync(rule, ct);
    }

    /// <summary>Sets the evaluation order within one scope: the listed rules get priorities 0, 10, 20, ...</summary>
    public async Task<List<RuleView>> ReorderAsync(ClaimsPrincipal user, RoutingRuleReorderRequest input, CancellationToken ct)
    {
        var ids = input.RuleIds.Distinct().ToArray();
        var scopeId = input.Scope == RoutingScope.Global ? null : input.ScopeId;
        var list = await ctx.Db.RoutingRules.Include(r => r.Targets)
            .Where(r => ids.Contains(r.Id) && r.Scope == input.Scope && r.ScopeId == scopeId).ToListAsync(ct);
        if (list.Count != ids.Length || ids.Length != input.RuleIds.Length)
        {
            throw new ApiFaultException(400, "Alla regler måste finnas och höra till samma omfång, och får bara anges en gång.");
        }

        var before = list.OrderBy(r => r.Priority).Select(r => new RulePriorityAudit(r.Id, r.Priority)).ToList();
        for (var i = 0; i < ids.Length; i++)
        {
            var rule = list.Single(r => r.Id == ids[i]);
            rule.Priority = i * 10;
            rule.UpdatedAt = ctx.Now;
        }

        await ctx.SaveAsync(user, AuditActions.Reorder, AuditEntities.RoutingRule, $"{input.Scope}:{scopeId}", before,
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
        var deployments = await ctx.Db.ModelDeployments.AsNoTracking().Select(d => new { d.Name, d.Kind, d.IsEnabled }).ToListAsync(ct);
        ModelInfoView ModelInfo(string name) =>
            aliases.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a ? new ModelInfoView(name, true, "alias", a.Kind.ToString(), a.IsEnabled)
            : deployments.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } d ? new ModelInfoView(name, true, "deployment", d.Kind.ToString(), d.IsEnabled)
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
    /// Called when a team or department is about to be deleted. If rules are scoped to it the caller must say
    /// what to do (<see cref="ScopedRulesChoice"/>). Without a choice the delete fails with 409 and a machine-readable
    /// list of the rules and the choices. The changes are staged and saved with the delete.
    /// </summary>
    public async Task ApplyScopeRemovalAsync(ClaimsPrincipal user, RoutingScope scope, Guid scopeId, string? choice, string what, CancellationToken ct)
    {
        var rules = await ctx.Db.RoutingRules.Include(r => r.Targets).Where(r => r.Scope == scope && r.ScopeId == scopeId).ToListAsync(ct);
        if (rules.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(choice))
        {
            throw new ApiFaultException(409,
                $"{what} har {rules.Count} routingregel(er). Välj om reglerna ska tas bort eller inaktiveras tills en ny organisationsdel kopplas till dem.",
                new Dictionary<string, object?>
                {
                    ["code"] = ScopedRulesCode,
                    ["choices"] = EnumQuery.Names<ScopedRulesChoice>(),
                    ["rules"] = rules.OrderBy(r => r.Priority).Select(r => new { r.Id, r.Name, r.IsEnabled }).ToList(),
                });
        }

        if (!EnumQuery.TryParse<ScopedRulesChoice>(choice, out var parsed))
        {
            throw new ApiFaultException(400, $"routingRules måste vara {string.Join(" eller ", EnumQuery.Names<ScopedRulesChoice>().Select(c => $"'{c}'"))}.");
        }

        foreach (var rule in rules)
        {
            var before = RoutingRuleAudit.Of(rule);
            if (parsed == ScopedRulesChoice.Delete)
            {
                ctx.Db.RoutingRules.Remove(rule);
                ctx.StageAudit(user, AuditActions.Delete, AuditEntities.RoutingRule, rule.Id, before, new RuleScopeRemovalAudit($"{what} {scope} {scopeId} togs bort"));
            }
            else
            {
                rule.IsEnabled = false;
                rule.UpdatedAt = ctx.Now;
                ctx.StageAudit(user, AuditActions.Deactivate, AuditEntities.RoutingRule, rule.Id, before, new RuleScopeRemovalAudit($"{what} {scope} {scopeId} togs bort; väntar på ny koppling", false));
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
            throw new ApiFaultException(409, $"'{name}' används av routingregeln '{user.Name}' och kan inte {action}. Ändra regeln först.");
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
                ?? problems.UnknownTargets.Select(t => $"okänd modell eller alias '{t}'").FirstOrDefault()
                ?? problems.UnknownFallbacks.Select(f => $"okänd reservmodell eller alias '{f}'").FirstOrDefault();
            if (problem is not null)
            {
                throw new ApiFaultException(400, $"Routingregeln '{input.Name}' är ogiltig: {problem.TrimEnd('.')}.");
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
    /// A rule as written by the API or an import: names trimmed, empty and duplicate fallbacks dropped. The condition is
    /// checked as typed (error positions refer to the text the user sees) and stored trimmed.
    /// </summary>
    private sealed record RuleInput(
        string Name, string? Description, bool IsEnabled, int Priority, RoutingScope Scope, Guid? ScopeId, string Condition, bool Chain,
        IReadOnlyList<RuleTarget> Targets, IReadOnlyList<string> Fallbacks)
    {
        public static RuleInput From(RoutingRuleRequest r) =>
            Create(r.Name, r.Description, r.IsEnabled, r.Priority, r.Scope, r.ScopeId, r.Condition, r.Chain, r.Targets, r.Fallbacks);

        /// <summary>Imported rules are always global: scoped rules' ids differ between environments.</summary>
        public static RuleInput From(ConfigRoutingRule r) =>
            Create(r.Name, r.Description, r.IsEnabled, r.Priority, RoutingScope.Global, null, r.Condition, r.Chain, r.Targets, r.Fallbacks);

        private static RuleInput Create(string name, string? description, bool isEnabled, int priority, RoutingScope scope, Guid? scopeId,
            string? condition, bool chain, IEnumerable<RuleTargetRequest> targets, IEnumerable<string>? fallbacks) => new(
            name.Trim(), description, isEnabled, priority, scope, scope == RoutingScope.Global ? null : scopeId, condition ?? "", chain,
            [.. targets.Select(t => new RuleTarget(t.Model.Trim(), t.Weight))],
            [.. (fallbacks ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);

        public RoutingRuleDefinition Definition(Guid id) => new(id, Name, IsEnabled, Priority, Scope, ScopeId, Condition, Chain, Targets, Fallbacks);
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
        rule.ScopeId = input.ScopeId;
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
        ?? throw new ApiFaultException(404, "Regeln finns inte.");

    /// <summary>Validates the request (field errors are a validation fault) and writes it onto the rule.</summary>
    private async Task ApplyRequestAsync(RoutingRule rule, RoutingRuleRequest request, bool isNew, CancellationToken ct)
    {
        var input = RuleInput.From(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var scopeChanged = isNew || rule.Scope != input.Scope || rule.ScopeId != input.ScopeId;
        await ValidateScopeAsync(request.Scope, request.ScopeId, mustExist: input.IsEnabled || scopeChanged, errors, ct);

        var problems = Check(rule.Id, input, await KnownModelNamesAsync(ct));
        foreach (var error in problems.Definition)
        {
            var key = error.Expression is not null ? "condition" : "request";
            var text = error.Expression is { } e ? $"{e.Message} (tecken {e.Position + 1})" : error.Message;
            errors[key] = [.. errors.GetValueOrDefault(key, []), text];
        }

        if (problems.UnknownTargets.Count > 0)
        {
            errors["targets"] = [$"Okänd modell eller alias: {string.Join(", ", problems.UnknownTargets)}. Endast kedjade regler får peka på namn som en annan regel hanterar."];
        }

        if (problems.UnknownFallbacks.Count > 0)
        {
            errors["fallbacks"] = [$"Okänd modell eller alias: {string.Join(", ", problems.UnknownFallbacks)}."];
        }

        if (errors.Count > 0)
        {
            throw ApiFaultException.Validation(errors);
        }

        await EnsureNameFreeAsync(input.Name, input.Scope, input.ScopeId, rule.Id, ct);
        Write(rule, input);
    }

    private async Task ValidateScopeAsync(RoutingScope scope, Guid? scopeId, bool mustExist, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (scope == RoutingScope.Global)
        {
            if (scopeId is not null)
            {
                errors["scopeId"] = ["En global regel ska inte ha något scopeId."];
            }

            return;
        }

        if (Owner.Of(scope, scopeId) is not { } owner)
        {
            errors["scopeId"] = ["Ange vilken nyckel, vilket team eller vilken förvaltning regeln gäller."];
            return;
        }

        if (mustExist && !await owners.ExistsAsync(owner, ct))
        {
            errors["scopeId"] = [$"{OwnerScopeResolver.Label(owner.Kind)} finns inte. Koppla regeln till en befintlig innan den aktiveras."];
        }
    }

    private async Task EnsureNameFreeAsync(string name, RoutingScope scope, Guid? scopeId, Guid self, CancellationToken ct)
    {
        var effectiveScopeId = scope == RoutingScope.Global ? null : scopeId;
        var lower = name.ToLowerInvariant();
#pragma warning disable CA1862, CA1304, CA1311 // the comparison must be translatable to SQL (lower() in the database)
        if (await ctx.Db.RoutingRules.AnyAsync(r => r.Id != self && r.Scope == scope && r.ScopeId == effectiveScopeId && r.Name.ToLower() == lower, ct))
#pragma warning restore CA1862, CA1304, CA1311
        {
            throw new ApiFaultException(409, "Det finns redan en regel med samma namn i detta omfång.");
        }
    }

    private async Task<HashSet<string>> KnownModelNamesAsync(CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(await ctx.Db.RouteAliases.AsNoTracking().Select(a => a.Name).ToListAsync(ct));
        names.UnionWith(await ctx.Db.ModelDeployments.AsNoTracking().Select(d => d.Name).ToListAsync(ct));
        return names;
    }

    private async Task<RuleView> ViewAsync(RoutingRule rule, CancellationToken ct) => (await ViewsAsync([rule], ct))[0];

    private async Task<List<RuleView>> ViewsAsync(IReadOnlyCollection<RoutingRule> rules, CancellationToken ct)
    {
        var names = await owners.NamesAsync(rules.Select(r => Owner.Of(r.Scope, r.ScopeId)).OfType<Owner>(), null, ct);
        return [.. rules.Select(r => View(r, names))];
    }

    private static RuleView View(RoutingRule r, Dictionary<Owner, string> names)
    {
        string? scopeName = Owner.Of(r.Scope, r.ScopeId) is { } owner && names.TryGetValue(owner, out var n) ? n : null;
        var orphaned = r.Scope != RoutingScope.Global && scopeName is null;
        return new RuleView(
            r.Id, r.Name, r.Description, r.IsEnabled, r.Priority, r.Scope.ToString(), r.ScopeId, scopeName, orphaned, r.Condition, r.Chain,
            [.. r.Targets.OrderByDescending(t => t.Weight).ThenBy(t => t.Model, StringComparer.OrdinalIgnoreCase).Select(t => new RuleTargetView(t.Model, t.Weight))],
            r.Fallbacks,
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
                ?? throw new ApiFaultException(404, "Nyckeln finns inte.");
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
