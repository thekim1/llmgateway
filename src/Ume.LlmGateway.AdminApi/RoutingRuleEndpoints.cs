using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;

namespace Ume.LlmGateway.AdminApi;

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
    Guid? ScopeId = null,
    [property: StringLength(RoutingRuleLimits.MaxDescriptionLength)] string? Description = null,
    bool IsEnabled = true,
    [property: Range(-RoutingRuleLimits.MaxPriority, RoutingRuleLimits.MaxPriority)] int Priority = 0,
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

/// <summary>Administration of routing rules (see <see cref="RoutingRuleService"/> and docs/routing-rules.md).</summary>
public static class RoutingRuleEndpoints
{
    public static void MapRoutingRules(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/routing-rules").RequireAuthorization("admin").WithTags("Routing rules");

        rules.MapGet("", async (RoutingScope? scope, Guid? scopeId, bool? orphaned, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(scope, scopeId, orphaned, ct)))
            .WithName("ListRoutingRules").WithSummary("Rules in evaluation order per scope");
        rules.MapGet("/{id:guid}", async (Guid id, RoutingRuleService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetRoutingRule");
        rules.MapPost("", async (RoutingRuleRequest input, RoutingRuleService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var rule = await service.CreateAsync(user, input, ct);
            return TypedResults.Created($"/api/routing-rules/{rule.Id}", rule);
        }).WithName("CreateRoutingRule").ProducesValidationProblem();
        rules.MapPut("/{id:guid}", async (Guid id, RoutingRuleRequest input, RoutingRuleService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(user, id, input, ct)))
            .WithName("UpdateRoutingRule").ProducesValidationProblem();
        rules.MapDelete("/{id:guid}", async (Guid id, RoutingRuleService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await service.DeleteAsync(user, id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteRoutingRule");
        rules.MapPost("/{id:guid}/reassign", async (Guid id, RoutingRuleReassignRequest input, RoutingRuleService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ReassignAsync(user, id, input, ct)))
            .WithName("ReassignRoutingRule").WithSummary("Attaches a rule (typically one deactivated with its team or department) to a new owner")
            .ProducesValidationProblem();
        rules.MapPost("/reorder", async (RoutingRuleReorderRequest input, RoutingRuleService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ReorderAsync(user, input, ct)))
            .WithName("ReorderRoutingRules").WithSummary("Sets the evaluation order within one scope (priorities 0, 10, 20, ...)");
        rules.MapPost("/validate", (ConditionCheckRequest input) => TypedResults.Ok(RoutingRuleService.CheckCondition(input.Condition)))
            .WithName("ValidateRoutingCondition").WithSummary("Live validation of a condition while it is being typed");
        rules.MapPost("/test", async (RoutingRuleTestRequest input, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.TestAsync(input, ct)))
            .WithName("TestRoutingRules").WithSummary("Dry run: what the stored, enabled rules would do with a request, and why");
    }
}
