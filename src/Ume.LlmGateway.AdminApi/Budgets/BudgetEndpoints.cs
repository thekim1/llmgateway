using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Budgets;

/// <summary>Budgets and their alerts (see <see cref="BudgetAdminService"/>).</summary>
public static class BudgetEndpoints
{
    public static void MapBudgets(this RouteGroupBuilder api)
    {
        var budgets = api.MapGroup("").RequireAuthorization(AdminPolicies.Read).WithTags("Budgets");
        budgets.MapGet("/budgets", async (BudgetScope? scope, Guid? scopeId, BudgetAdminService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(scope, scopeId, ct)))
            .WithName("ListBudgets").WithSummary("Budgets the user may see, with spend in the current period");
        budgets.MapPost("/budgets", async (BudgetRequest input, BudgetAdminService service, CancellationToken ct) =>
        {
            var budget = await service.CreateAsync(input, ct);
            return TypedResults.Created($"/api/budgets/{budget.Id}", budget);
        }).RequireAuthorization(AdminPolicies.Manage).WithName("CreateBudget");
        budgets.MapPut("/budgets/{id:guid}", async (Guid id, BudgetRequest input, BudgetAdminService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
            .RequireAuthorization(AdminPolicies.Manage).WithName("UpdateBudget");
        budgets.MapDelete("/budgets/{id:guid}", async (Guid id, BudgetAdminService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Manage).WithName("DeleteBudget");
        budgets.MapGet("/alerts", async (bool? acknowledged, BudgetAdminService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AlertsAsync(acknowledged, ct)))
            .WithName("ListAlerts").WithSummary("Budget alerts the user may see, newest first");
        budgets.MapPost("/alerts/{id:guid}/acknowledge", async (Guid id, BudgetAdminService service, CancellationToken ct) =>
        {
            await service.AcknowledgeAsync(id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Manage).WithName("AcknowledgeAlert");
    }
}
