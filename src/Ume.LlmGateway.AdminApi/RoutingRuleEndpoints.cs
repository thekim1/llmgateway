using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record RuleTargetRequest(
    [property: Required, StringLength(200)] string Model,
    [property: Range(1, RoutingRuleSet.MaxWeight)] int Weight = 1) : AdminRequest;

public sealed record RoutingRuleRequest(
    [property: Required, StringLength(200)] string Name,
    [property: EnumDataType(typeof(RoutingScope))] RoutingScope Scope,
    [property: Required, MinLength(1), MaxLength(RoutingRuleSet.MaxTargets)] RuleTargetRequest[] Targets,
    Guid? ScopeId = null,
    [property: StringLength(1000)] string? Description = null,
    bool IsEnabled = true,
    [property: Range(-1_000_000, 1_000_000)] int Priority = 0,
    [property: StringLength(RoutingExpression.MaxLength)] string? Condition = null,
    bool Chain = false,
    [property: MaxLength(RoutingRuleSet.MaxTargets)] string[]? Fallbacks = null) : AdminRequest;

public sealed record RoutingRuleReassignRequest(
    [property: EnumDataType(typeof(RoutingScope))] RoutingScope Scope,
    Guid? ScopeId,
    bool Enable = true) : AdminRequest;

public sealed record RoutingRuleReorderRequest(
    [property: EnumDataType(typeof(RoutingScope))] RoutingScope Scope,
    Guid? ScopeId,
    [property: Required, MinLength(1), MaxLength(1000)] Guid[] RuleIds) : AdminRequest;

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

/// <summary>Administration of routing rules. See docs/routing-rules.md.</summary>
public static class RoutingRuleEndpoints
{
    public const string ScopedRulesCode = "routing_rules_scoped";

    private static readonly string[] Choices = ["delete", "deactivate"];

    public static void MapRoutingRules(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/routing-rules").RequireAuthorization("admin");

        rules.MapGet("", async (RoutingScope? scope, Guid? scopeId, bool? orphaned, AdminContext ctx, CancellationToken ct) =>
        {
            var query = ctx.Db.RoutingRules.Include(r => r.Targets).AsQueryable();
            if (scope is { } s) { query = query.Where(r => r.Scope == s); }
            if (scopeId is { } id) { query = query.Where(r => r.ScopeId == id); }
            var list = await query.ToListAsync(ct);
            var names = await ScopeNamesAsync(ctx, list, ct);
            var items = list
                .OrderBy(r => (int)r.Scope).ThenBy(r => r.ScopeId).ThenBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => RuleDto(r, names))
                .ToList();
            return orphaned is { } o ? items.Where(i => i.IsOrphaned == o) : (IEnumerable<RuleView>)items;
        });

        rules.MapGet("/{id:guid}", async (Guid id, AdminContext ctx, CancellationToken ct) =>
        {
            var rule = await FindAsync(ctx, id, ct);
            return RuleDto(rule, await ScopeNamesAsync(ctx, [rule], ct));
        });

        rules.MapPost("", async (RoutingRuleRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var rule = new RoutingRule { Name = input.Name.Trim(), CreatedAt = ctx.Now, UpdatedAt = ctx.Now };
            if (await ApplyAsync(rule, input, ctx, isNew: true, ct) is { } problem) { return problem; }
            ctx.Db.RoutingRules.Add(rule);
            await ctx.SaveAsync(user, "create", "RoutingRule", rule.Id, null, AuditView(rule), InvalidationKind.Config, ct);
            return Results.Created($"/api/routing-rules/{rule.Id}", RuleDto(rule, await ScopeNamesAsync(ctx, [rule], ct)));
        });

        rules.MapPut("/{id:guid}", async (Guid id, RoutingRuleRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var rule = await FindAsync(ctx, id, ct);
            var before = AuditView(rule);
            if (await ApplyAsync(rule, input, ctx, isNew: false, ct) is { } problem) { return problem; }
            await ctx.SaveAsync(user, "update", "RoutingRule", id, before, AuditView(rule), InvalidationKind.Config, ct);
            return Results.Ok(RuleDto(rule, await ScopeNamesAsync(ctx, [rule], ct)));
        });

        rules.MapDelete("/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var rule = await FindAsync(ctx, id, ct);
            ctx.Db.RoutingRules.Remove(rule);
            await ctx.SaveAsync(user, "delete", "RoutingRule", id, AuditView(rule), null, InvalidationKind.Config, ct);
            return Results.NoContent();
        });

        // Attaches a rule (typically one deactivated when its team or department was removed) to a new owner.
        rules.MapPost("/{id:guid}/reassign", async (Guid id, RoutingRuleReassignRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var rule = await FindAsync(ctx, id, ct);
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            await ValidateScopeAsync(ctx, input.Scope, input.ScopeId, mustExist: true, errors, ct);
            if (errors.Count > 0) { return Results.ValidationProblem(errors, title: "Kontrollera de markerade fälten."); }
            await EnsureNameFreeAsync(ctx, rule.Name, input.Scope, input.ScopeId, rule.Id, ct);

            var before = AuditView(rule);
            rule.Scope = input.Scope;
            rule.ScopeId = input.Scope == RoutingScope.Global ? null : input.ScopeId;
            rule.IsEnabled = input.Enable;
            rule.UpdatedAt = ctx.Now;
            await ctx.SaveAsync(user, "reassign", "RoutingRule", id, before, AuditView(rule), InvalidationKind.Config, ct);
            return Results.Ok(RuleDto(rule, await ScopeNamesAsync(ctx, [rule], ct)));
        });

        // Sets the evaluation order within one scope: the listed rules get priorities 0, 10, 20, ...
        rules.MapPost("/reorder", async (RoutingRuleReorderRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var ids = input.RuleIds.Distinct().ToArray();
            var scopeId = input.Scope == RoutingScope.Global ? null : input.ScopeId;
            var list = await ctx.Db.RoutingRules.Include(r => r.Targets)
                .Where(r => ids.Contains(r.Id) && r.Scope == input.Scope && r.ScopeId == scopeId).ToListAsync(ct);
            if (list.Count != ids.Length || ids.Length != input.RuleIds.Length)
            {
                throw new AdminFaultException(400, "Alla regler måste finnas och höra till samma omfång, och får bara anges en gång.");
            }

            var before = list.OrderBy(r => r.Priority).Select(r => new { r.Id, r.Priority }).ToList();
            for (var i = 0; i < ids.Length; i++)
            {
                var rule = list.Single(r => r.Id == ids[i]);
                rule.Priority = i * 10;
                rule.UpdatedAt = ctx.Now;
            }

            await ctx.SaveAsync(user, "reorder", "RoutingRule", $"{input.Scope}:{scopeId}", before, ids.Select((id, i) => new { Id = id, Priority = i * 10 }), InvalidationKind.Config, ct);
            var names = await ScopeNamesAsync(ctx, list, ct);
            return list.OrderBy(r => r.Priority).Select(r => RuleDto(r, names));
        });

        // Live validation of a condition while it is being typed.
        rules.MapPost("/validate", (ConditionCheckRequest input) =>
        {
            var (expression, errors) = RoutingExpression.Compile(input.Condition, RoutingSchema.Instance);
            return new
            {
                valid = errors.Count == 0,
                errors = errors.Select(e => new { e.Position, e.Length, e.Message }),
                variables = expression?.Variables.Order(StringComparer.Ordinal).ToArray() ?? [],
                available = RoutingSchema.Instance.Variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new { name = v.Key, type = v.Value.ToString() }),
            };
        });

        // Dry run: what would the stored, enabled rules do with this request, and why?
        rules.MapPost("/test", async (RoutingRuleTestRequest input, AdminContext ctx, CancellationToken ct) =>
        {
            var stored = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).ToListAsync(ct);
            var (set, buildErrors) = RoutingRuleSet.Build(stored.Select(r => r.ToDefinition()));
            var context = await TestContextAsync(input, ctx, ct);

            var explain = new List<RuleEvaluation>();
            var random = input.Seed is { } seed ? new Random(seed) : Random.Shared;
            var decision = set.Evaluate(context, random, explain);

            var aliases = await ctx.Db.RouteAliases.AsNoTracking().Select(a => new { a.Name, a.Kind, a.IsEnabled }).ToListAsync(ct);
            var deployments = await ctx.Db.ModelDeployments.AsNoTracking().Select(d => new { d.Name, d.Kind, d.IsEnabled }).ToListAsync(ct);
            ModelInfoView ModelInfo(string name) =>
                aliases.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a ? new ModelInfoView(name, true, "alias", a.Kind.ToString(), a.IsEnabled)
                : deployments.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } d ? new ModelInfoView(name, true, "deployment", d.Kind.ToString(), d.IsEnabled)
                : new ModelInfoView(name, false, "unknown", null, false);

            return new
            {
                matched = decision.Matched,
                primaryModel = decision.PrimaryModel,
                models = decision.Models.Select(ModelInfo),
                applied = decision.Applied.Select(a => new { a.RuleId, a.Name, a.FromModel, a.ToModel }),
                chainLimitReached = decision.ChainLimitReached,
                evaluation = explain.Select(e => new
                {
                    e.RuleId, e.Name, scope = e.Scope.ToString(), e.Priority, e.ChainStep, e.Model,
                    outcome = e.Outcome.ToString(),
                    trace = e.Trace.Select(t => new { t.Text, t.LeftValue, t.Result }),
                }),
                ignoredRules = buildErrors.Select(e => new { e.RuleId, e.RuleName, e.Message, position = e.Expression?.Position, length = e.Expression?.Length }),
                note = "Only the usage values you supply (budgetUsed, tokensUsed, piiDetected, ...) are known to the rules; a condition that uses a value you left out does not match.",
            };
        });
    }

    // ---- scope removal: ask the user what to do with the rules ------------------------------------------

    /// <summary>
    /// Called when a team or department is about to be deleted. If rules are scoped to it the caller must say
    /// what to do: <c>delete</c> them, or <c>deactivate</c> them until a new team/department is assigned (the rules
    /// often belong to a service and only the organisation changed). Without a choice the delete fails with 409 and a
    /// machine-readable list of the rules and the choices. The changes are staged and saved with the delete.
    /// </summary>
    public static async Task ApplyScopeRemovalAsync(AdminContext ctx, ClaimsPrincipal user, RoutingScope scope, Guid scopeId, string? choice, string what, CancellationToken ct)
    {
        var rules = await ctx.Db.RoutingRules.Include(r => r.Targets).Where(r => r.Scope == scope && r.ScopeId == scopeId).ToListAsync(ct);
        if (rules.Count == 0)
        {
            return;
        }

        var normalised = choice?.Trim().ToLowerInvariant();
        if (normalised is null or "")
        {
            throw new AdminFaultException(409,
                $"{what} har {rules.Count} routingregel(er). Välj om reglerna ska tas bort eller inaktiveras tills en ny organisationsdel kopplas till dem.",
                new Dictionary<string, object?>
                {
                    ["code"] = ScopedRulesCode,
                    ["choices"] = Choices,
                    ["rules"] = rules.OrderBy(r => r.Priority).Select(r => new { r.Id, r.Name, r.IsEnabled }).ToList(),
                });
        }

        if (!Choices.Contains(normalised))
        {
            throw new AdminFaultException(400, "routingRules måste vara 'delete' eller 'deactivate'.");
        }

        foreach (var rule in rules)
        {
            var before = AuditView(rule);
            if (normalised == "delete")
            {
                ctx.Db.RoutingRules.Remove(rule);
                ctx.StageAudit(user, "delete", "RoutingRule", rule.Id, before, new { reason = $"{what} {scope} {scopeId} togs bort" });
            }
            else
            {
                rule.IsEnabled = false;
                rule.UpdatedAt = ctx.Now;
                ctx.StageAudit(user, "deactivate", "RoutingRule", rule.Id, before, new { reason = $"{what} {scope} {scopeId} togs bort; väntar på ny koppling", isEnabled = false });
            }
        }
    }

    // ---- integrity: names used by rules cannot disappear -------------------------------------------------

    /// <summary>Throws 409 if a rule uses <paramref name="name"/> as target or fallback (a route alias or model being removed or renamed).</summary>
    public static async Task EnsureNotUsedByRulesAsync(AdminContext ctx, string name, string action, CancellationToken ct)
    {
        var rules = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).ToListAsync(ct);
        var user = rules.FirstOrDefault(r =>
            r.Targets.Any(t => string.Equals(t.Model, name, StringComparison.OrdinalIgnoreCase)) ||
            r.Fallbacks.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)));
        if (user is not null)
        {
            throw new AdminFaultException(409, $"'{name}' används av routingregeln '{user.Name}' och kan inte {action}. Ändra regeln först.");
        }
    }

    /// <summary>
    /// Imports global rules by name (create or update; rules not in the document are left alone, as with routes).
    /// Every rule is validated like one saved through the API; an invalid rule fails the whole import.
    /// <paramref name="importedNames"/> are routes and models created by the same import.
    /// </summary>
    public static async Task<(int Created, int Updated)> ImportGlobalAsync(AdminContext ctx, IReadOnlyCollection<ConfigRoutingRule> items, IReadOnlyCollection<string> importedNames, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return (0, 0);
        }

        var known = await KnownModelNamesAsync(ctx, ct);
        known.UnionWith(importedNames);
        var existing = await ctx.Db.RoutingRules.Include(r => r.Targets).Where(r => r.Scope == RoutingScope.Global).ToListAsync(ct);
        int created = 0, updated = 0;
        foreach (var item in items)
        {
            var name = item.Name.Trim();
            var targets = item.Targets.Select(t => new RuleTarget(t.Model.Trim(), t.Weight)).ToList();
            var fallbacks = (item.Fallbacks ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rule = existing.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var definition = new RoutingRuleDefinition(rule?.Id ?? Guid.Empty, name, item.IsEnabled, item.Priority, RoutingScope.Global, null, item.Condition ?? "", item.Chain, targets, fallbacks);
            var problem = RoutingRuleSet.Check(definition).Select(e => e.Message).FirstOrDefault()
                ?? targets.Where(t => !item.Chain && !known.Contains(t.Model)).Select(t => $"okänd modell eller alias '{t.Model}'").FirstOrDefault()
                ?? fallbacks.Where(f => !known.Contains(f)).Select(f => $"okänd reservmodell eller alias '{f}'").FirstOrDefault();
            if (problem is not null)
            {
                throw new AdminFaultException(400, $"Routingregeln '{name}' är ogiltig: {problem.TrimEnd('.')}.");
            }

            if (rule is null)
            {
                rule = new RoutingRule { Name = name, CreatedAt = ctx.Now };
                ctx.Db.RoutingRules.Add(rule);
                created++;
            }
            else
            {
                ctx.Db.RoutingRuleTargets.RemoveRange(rule.Targets);
                updated++;
            }

            rule.Name = name;
            rule.Description = item.Description;
            rule.IsEnabled = item.IsEnabled;
            rule.Priority = item.Priority;
            rule.Scope = RoutingScope.Global;
            rule.ScopeId = null;
            rule.Condition = (item.Condition ?? "").Trim();
            rule.Chain = item.Chain;
            rule.Fallbacks = fallbacks;
            rule.UpdatedAt = ctx.Now;
            rule.Targets = [.. targets.Select(t => new RoutingRuleTarget { RoutingRuleId = rule.Id, Model = t.Model, Weight = t.Weight })];
            ctx.Db.RoutingRuleTargets.AddRange(rule.Targets);
        }

        return (created, updated);
    }

    // ---- internals ---------------------------------------------------------------------------------------

    private static async Task<RoutingRule> FindAsync(AdminContext ctx, Guid id, CancellationToken ct) =>
        await ctx.Db.RoutingRules.Include(r => r.Targets).SingleOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new AdminFaultException(404, "Regeln finns inte.");

    private static async Task<IResult?> ApplyAsync(RoutingRule rule, RoutingRuleRequest input, AdminContext ctx, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = input.Name.Trim();
        var scopeId = input.Scope == RoutingScope.Global ? null : input.ScopeId;
        var scopeChanged = isNew || rule.Scope != input.Scope || rule.ScopeId != scopeId;

        await ValidateScopeAsync(ctx, input.Scope, input.ScopeId, mustExist: input.IsEnabled || scopeChanged, errors, ct);

        var targets = input.Targets.Select(t => new RuleTarget(t.Model.Trim(), t.Weight)).ToList();
        var fallbacks = (input.Fallbacks ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var definition = new RoutingRuleDefinition(rule.Id, name, input.IsEnabled, input.Priority, input.Scope, scopeId, input.Condition ?? "", input.Chain, targets, fallbacks);
        foreach (var error in RoutingRuleSet.Check(definition))
        {
            var key = error.Expression is not null ? "condition" : "request";
            var text = error.Expression is { } e ? $"{e.Message} (tecken {e.Position + 1})" : error.Message;
            errors[key] = [.. errors.GetValueOrDefault(key, []), text];
        }

        var known = await KnownModelNamesAsync(ctx, ct);
        var unknownTargets = targets.Where(t => !known.Contains(t.Model)).Select(t => t.Model).ToList();
        if (unknownTargets.Count > 0 && !input.Chain)
        {
            errors["targets"] = [$"Okänd modell eller alias: {string.Join(", ", unknownTargets)}. Endast kedjade regler får peka på namn som en annan regel hanterar."];
        }

        var unknownFallbacks = fallbacks.Where(f => !known.Contains(f)).ToList();
        if (unknownFallbacks.Count > 0)
        {
            errors["fallbacks"] = [$"Okänd modell eller alias: {string.Join(", ", unknownFallbacks)}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "Kontrollera de markerade fälten.");
        }

        await EnsureNameFreeAsync(ctx, name, input.Scope, scopeId, rule.Id, ct);

        rule.Name = name;
        rule.Description = input.Description;
        rule.IsEnabled = input.IsEnabled;
        rule.Priority = input.Priority;
        rule.Scope = input.Scope;
        rule.ScopeId = scopeId;
        rule.Condition = (input.Condition ?? "").Trim();
        rule.Chain = input.Chain;
        rule.Fallbacks = fallbacks;
        rule.UpdatedAt = ctx.Now;
        ctx.Db.RoutingRuleTargets.RemoveRange(rule.Targets);
        rule.Targets = [.. targets.Select(t => new RoutingRuleTarget { RoutingRuleId = rule.Id, Model = t.Model, Weight = t.Weight })];
        ctx.Db.RoutingRuleTargets.AddRange(rule.Targets);
        return null;
    }

    private static async Task ValidateScopeAsync(AdminContext ctx, RoutingScope scope, Guid? scopeId, bool mustExist, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (scope == RoutingScope.Global)
        {
            if (scopeId is not null)
            {
                errors["scopeId"] = ["En global regel ska inte ha något scopeId."];
            }

            return;
        }

        if (scopeId is null)
        {
            errors["scopeId"] = ["Ange vilken nyckel, vilket team eller vilken förvaltning regeln gäller."];
            return;
        }

        if (mustExist && !await ScopeExistsAsync(ctx, scope, scopeId.Value, ct))
        {
            errors["scopeId"] = [$"{ScopeLabel(scope)} finns inte. Koppla regeln till en befintlig innan den aktiveras."];
        }
    }

    private static async Task<bool> ScopeExistsAsync(AdminContext ctx, RoutingScope scope, Guid id, CancellationToken ct) => scope switch
    {
        RoutingScope.VirtualKey => await ctx.Db.VirtualKeys.AnyAsync(k => k.Id == id, ct),
        RoutingScope.Team => await ctx.Db.Teams.AnyAsync(t => t.Id == id, ct),
        RoutingScope.Department => await ctx.Db.Departments.AnyAsync(d => d.Id == id, ct),
        _ => true,
    };

    private static string ScopeLabel(RoutingScope scope) => scope switch
    {
        RoutingScope.VirtualKey => "Nyckeln",
        RoutingScope.Team => "Teamet",
        RoutingScope.Department => "Förvaltningen",
        _ => "Omfånget",
    };

    private static async Task EnsureNameFreeAsync(AdminContext ctx, string name, RoutingScope scope, Guid? scopeId, Guid self, CancellationToken ct)
    {
        var effectiveScopeId = scope == RoutingScope.Global ? null : scopeId;
        var lower = name.ToLowerInvariant();
#pragma warning disable CA1862 // the comparison must be translatable to SQL
        if (await ctx.Db.RoutingRules.AnyAsync(r => r.Id != self && r.Scope == scope && r.ScopeId == effectiveScopeId && r.Name.ToLower() == lower, ct))
#pragma warning restore CA1862
        {
            throw new AdminFaultException(409, "Det finns redan en regel med samma namn i detta omfång.");
        }
    }

    private static async Task<HashSet<string>> KnownModelNamesAsync(AdminContext ctx, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(await ctx.Db.RouteAliases.AsNoTracking().Select(a => a.Name).ToListAsync(ct));
        names.UnionWith(await ctx.Db.ModelDeployments.AsNoTracking().Select(d => d.Name).ToListAsync(ct));
        return names;
    }

    private static async Task<Dictionary<(RoutingScope, Guid), string>> ScopeNamesAsync(AdminContext ctx, IReadOnlyCollection<RoutingRule> rules, CancellationToken ct)
    {
        var names = new Dictionary<(RoutingScope, Guid), string>();
        Guid[] Ids(RoutingScope s) => [.. rules.Where(r => r.Scope == s && r.ScopeId is not null).Select(r => r.ScopeId!.Value).Distinct()];
        var keys = Ids(RoutingScope.VirtualKey);
        var teams = Ids(RoutingScope.Team);
        var departments = Ids(RoutingScope.Department);
        foreach (var k in await ctx.Db.VirtualKeys.AsNoTracking().Where(k => keys.Contains(k.Id)).Select(k => new { k.Id, k.Name }).ToListAsync(ct)) { names[(RoutingScope.VirtualKey, k.Id)] = k.Name; }
        foreach (var t in await ctx.Db.Teams.AsNoTracking().Where(t => teams.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToListAsync(ct)) { names[(RoutingScope.Team, t.Id)] = t.Name; }
        foreach (var d in await ctx.Db.Departments.AsNoTracking().Where(d => departments.Contains(d.Id)).Select(d => new { d.Id, d.Name }).ToListAsync(ct)) { names[(RoutingScope.Department, d.Id)] = d.Name; }
        return names;
    }

    public sealed record RuleView(
        Guid Id, string Name, string? Description, bool IsEnabled, int Priority, string Scope, Guid? ScopeId, string? ScopeName,
        bool IsOrphaned, string Condition, bool Chain, IReadOnlyList<RuleTargetView> Targets, IReadOnlyList<string> Fallbacks,
        IReadOnlyList<RuleProblem> ValidationErrors, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record RuleTargetView(string Model, int Weight);

    public sealed record ModelInfoView(string Name, bool Exists, string Type, string? Kind, bool Enabled);

    public sealed record RuleProblem(string Message, int? Position, int? Length);

    /// <summary>
    /// <c>IsOrphaned</c>: the key, team or department the rule is scoped to no longer exists. Such a rule never applies
    /// (and cannot be enabled) until it is reassigned. <c>ValidationErrors</c> lists why the gateway ignores an enabled rule.
    /// </summary>
    public static RuleView RuleDto(RoutingRule r, IReadOnlyDictionary<(RoutingScope, Guid), string> names)
    {
        string? scopeName = r.ScopeId is { } id && names.TryGetValue((r.Scope, id), out var n) ? n : null;
        var orphaned = r.Scope != RoutingScope.Global && scopeName is null;
        return new RuleView(
            r.Id, r.Name, r.Description, r.IsEnabled, r.Priority, r.Scope.ToString(), r.ScopeId, scopeName, orphaned, r.Condition, r.Chain,
            [.. r.Targets.OrderByDescending(t => t.Weight).ThenBy(t => t.Model, StringComparer.OrdinalIgnoreCase).Select(t => new RuleTargetView(t.Model, t.Weight))],
            r.Fallbacks,
            [.. RoutingRuleSet.Check(r.ToDefinition()).Select(e => new RuleProblem(e.Message, e.Expression?.Position, e.Expression?.Length))],
            r.CreatedAt, r.UpdatedAt);
    }

    private static object AuditView(RoutingRule r) => new
    {
        r.Id, r.Name, r.IsEnabled, r.Priority, scope = r.Scope.ToString(), r.ScopeId, r.Condition, r.Chain,
        targets = r.Targets.Select(t => new { t.Model, t.Weight }).ToArray(), fallbacks = r.Fallbacks.ToArray(),
    };

    private static async Task<RoutingContext> TestContextAsync(RoutingRuleTestRequest input, AdminContext ctx, CancellationToken ct)
    {
        Guid? teamId = input.TeamId, departmentId = input.DepartmentId, keyId = input.KeyId;
        string? keyName = null, teamName = null, departmentName = null;
        IReadOnlySet<Guid>? lineage = null;

        if (keyId is { } kid)
        {
            var key = await ctx.Db.VirtualKeys.AsNoTracking().Include(k => k.Team!).ThenInclude(t => t.Department).SingleOrDefaultAsync(k => k.Id == kid, ct)
                ?? throw new AdminFaultException(404, "Nyckeln finns inte.");
            keyName = key.Name;
            teamId ??= key.TeamId;
            departmentId ??= key.Team?.DepartmentId;
            var replacements = await ctx.Db.VirtualKeys.AsNoTracking().Where(k => k.RotatedToKeyId != null).ToDictionaryAsync(k => k.Id, k => k.RotatedToKeyId!.Value, ct);
            lineage = KeyRotation.Ancestors(kid, replacements);
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
            object? converted = value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String => value.GetString(),
                _ => null,
            };
            parameters[name] = converted;
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
