using Ume.LlmGateway.AdminApi.Security;

namespace Ume.LlmGateway.AdminApi.Organisation;

/// <summary>Departments and teams (see <see cref="OrganisationService"/>).</summary>
public static class OrganisationEndpoints
{
    public static void MapOrganisation(this RouteGroupBuilder root)
    {
        // Team admins list their own teams (to pick one for a key); everything else here needs a role that reads.
        root.MapGet("/teams", async (Guid? departmentId, OrganisationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.TeamsAsync(departmentId, ct)))
            .RequireAuthorization(AdminPolicies.ListTeams).WithTags("Organisation").WithName("ListTeams");
        var api = root.MapGroup("").RequireAuthorization(AdminPolicies.Read).WithTags("Organisation");
        api.MapGet("/departments", async (OrganisationService service, CancellationToken ct) => TypedResults.Ok(await service.DepartmentsAsync(ct)))
            .WithName("ListDepartments");
        api.MapPost("/departments", async (DepartmentRequest input, OrganisationService service, CancellationToken ct) =>
        {
            var department = await service.CreateDepartmentAsync(input, ct);
            return TypedResults.Created($"/api/departments/{department.Id}", department);
        }).RequireAuthorization(AdminPolicies.Admin).WithName("CreateDepartment");
        api.MapPut("/departments/{id:guid}", async (Guid id, DepartmentRequest input, OrganisationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateDepartmentAsync(id, input, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateDepartment");
        api.MapDelete("/departments/{id:guid}", async (Guid id, string? routingRules, OrganisationService service, CancellationToken ct) =>
        {
            await service.DeleteDepartmentAsync(id, routingRules, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Admin).WithName("DeleteDepartment")
            .WithSummary("Deletes an empty department; routingRules=delete|deactivate says what happens to its routing rules");
        api.MapPost("/teams", async (TeamRequest input, OrganisationService service, CancellationToken ct) =>
        {
            var team = await service.CreateTeamAsync(input, ct);
            return TypedResults.Created($"/api/teams/{team.Id}", team);
        }).RequireAuthorization(AdminPolicies.Manage).WithName("CreateTeam");
        api.MapPut("/teams/{id:guid}", async (Guid id, TeamRequest input, OrganisationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateTeamAsync(id, input, ct)))
            .RequireAuthorization(AdminPolicies.Manage).WithName("UpdateTeam");
        api.MapDelete("/teams/{id:guid}", async (Guid id, string? routingRules, OrganisationService service, CancellationToken ct) =>
        {
            await service.DeleteTeamAsync(id, routingRules, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Manage).WithName("DeleteTeam")
            .WithSummary("Deletes a team without keys; routingRules=delete|deactivate says what happens to its routing rules");
    }
}
