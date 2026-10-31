using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain.Routing;

namespace Ume.LlmGateway.AdminApi.RoutingRules;

/// <summary>Administration of routing rules (see <see cref="RoutingRuleService"/> and docs/routing-rules.md).</summary>
public static class RoutingRuleEndpoints
{
    public static void MapRoutingRules(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/routing-rules").RequireAuthorization(AdminPolicies.Admin).WithTags("Routing rules");

        rules.MapGet("", async (RoutingScope? scope, Guid? scopeId, bool? orphaned, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(scope, scopeId, orphaned, ct)))
            .WithName("ListRoutingRules").WithSummary("Rules in evaluation order per scope");
        rules.MapGet("/{id:guid}", async (Guid id, RoutingRuleService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetRoutingRule");
        rules.MapPost("", async (RoutingRuleRequest input, RoutingRuleService service, CancellationToken ct) =>
        {
            var rule = await service.CreateAsync(input, ct);
            return TypedResults.Created($"/api/routing-rules/{rule.Id}", rule);
        }).WithName("CreateRoutingRule").ProducesValidationProblem();
        rules.MapPut("/{id:guid}", async (Guid id, RoutingRuleRequest input, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
            .WithName("UpdateRoutingRule").ProducesValidationProblem();
        rules.MapDelete("/{id:guid}", async (Guid id, RoutingRuleService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteRoutingRule");
        rules.MapPut("/{id:guid}/enabled", async (Guid id, RoutingRuleEnabledRequest input, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SetEnabledAsync(id, input.IsEnabled, ct)))
            .WithName("SetRoutingRuleEnabled").WithSummary("Turns a rule on or off; turning it on checks that its owners and models exist");
        rules.MapPost("/{id:guid}/reassign", async (Guid id, RoutingRuleReassignRequest input, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ReassignAsync(id, input, ct)))
            .WithName("ReassignRoutingRule").WithSummary("Attaches a rule (typically one deactivated with its last team or department) to new owners")
            .ProducesValidationProblem();
        rules.MapPost("/reorder", async (RoutingRuleReorderRequest input, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ReorderAsync(input, ct)))
            .WithName("ReorderRoutingRules").WithSummary("Sets the evaluation order within one scope type (priorities 0, 10, 20, ...)");
        rules.MapPost("/validate", (ConditionCheckRequest input) => TypedResults.Ok(RoutingRuleService.CheckCondition(input.Condition)))
            .WithName("ValidateRoutingCondition").WithSummary("Live validation of a condition while it is being typed");
        rules.MapPost("/test", async (RoutingRuleTestRequest input, RoutingRuleService service, CancellationToken ct) =>
            TypedResults.Ok(await service.TestAsync(input, ct)))
            .WithName("TestRoutingRules").WithSummary("Dry run: what the stored, enabled rules would do with a request, and why");
    }
}
