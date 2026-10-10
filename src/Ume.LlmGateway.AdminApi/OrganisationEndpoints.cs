using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record DepartmentRequest(
    [property: Required, StringLength(200)] string Name,
    [property: Required, StringLength(50)] string CostCenterCode,
    bool IsActive = true) : AdminRequest;
public sealed record TeamRequest(
    Guid DepartmentId,
    [property: Required, StringLength(200)] string Name,
    [property: StringLength(1000)] string? Description = null,
    bool IsActive = true) : AdminRequest;

public static class OrganisationEndpoints
{
    public static void MapOrganisation(this RouteGroupBuilder api)
    {
        api = api.MapGroup("").RequireAuthorization("read").WithTags("Organisation");
        api.MapGet("/departments", async (AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok((await ctx.Departments(user).Include(d => d.Teams).OrderBy(d => d.Name).ToListAsync(ct)).Select(DepartmentDto.Of).ToList()))
            .WithName("ListDepartments");
        api.MapPost("/departments", async (DepartmentRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = new Department { Name = input.Name.Trim(), CostCenterCode = input.CostCenterCode.Trim(), CreatedAt = ctx.Now };
            ctx.Db.Departments.Add(d);
            await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.Department, d.Id, null, DepartmentAudit.Of(d), InvalidationKind.Config, ct);
            return TypedResults.Created($"/api/departments/{d.Id}", DepartmentDto.Of(d));
        }).RequireAuthorization("admin").WithName("CreateDepartment");
        api.MapPut("/departments/{id:guid}", async (Guid id, DepartmentRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = await ctx.Db.Departments.Include(d => d.Teams).SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new ApiFaultException(404, "Förvaltningen finns inte.");
            var before = DepartmentAudit.Of(d);
            d.Name = input.Name.Trim(); d.CostCenterCode = input.CostCenterCode.Trim(); d.IsActive = input.IsActive;
            // Keys too: an authenticated key carries its department's name and state.
            await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.Department, id, before, DepartmentAudit.Of(d), [InvalidationKind.Config, InvalidationKind.Keys], ct);
            return TypedResults.Ok(DepartmentDto.Of(d));
        }).RequireAuthorization("admin").WithName("UpdateDepartment");
        api.MapDelete("/departments/{id:guid}", async (Guid id, string? routingRules, AdminContext ctx, RoutingRuleService rules, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = await ctx.Db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new ApiFaultException(404, "Förvaltningen finns inte.");
            if (await ctx.Db.Teams.AnyAsync(t => t.DepartmentId == id, ct)) { throw new ApiFaultException(409, "Förvaltningen har team och kan inte tas bort."); }
            await rules.ApplyScopeRemovalAsync(user, RoutingScope.Department, id, routingRules, "Förvaltningen", ct);
            ctx.Db.Departments.Remove(d);
            await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.Department, id, DepartmentAudit.Of(d), null, InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization("admin").WithName("DeleteDepartment")
            .WithSummary("Deletes an empty department; routingRules=delete|deactivate says what happens to its routing rules");
        api.MapGet("/teams", async (Guid? departmentId, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var query = ctx.Teams(user).Include(t => t.VirtualKeys).AsQueryable();
            if (departmentId is { } id) { query = query.Where(t => t.DepartmentId == id); }
            return TypedResults.Ok((await query.OrderBy(t => t.Name).ToListAsync(ct)).Select(TeamDto.Of).ToList());
        }).WithName("ListTeams");
        api.MapPost("/teams", async (TeamRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = await ctx.Departments(user).SingleOrDefaultAsync(d => d.Id == input.DepartmentId, ct) ?? throw new ApiFaultException(404, "Förvaltningen finns inte.");
            var t = new Team { Department = d, DepartmentId = d.Id, Name = input.Name.Trim(), Description = input.Description, CreatedAt = ctx.Now };
            ctx.Db.Teams.Add(t);
            await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.Team, t.Id, null, TeamAudit.Of(t), InvalidationKind.Config, ct);
            return TypedResults.Created($"/api/teams/{t.Id}", TeamDto.Of(t));
        }).RequireAuthorization("manage").WithName("CreateTeam");
        api.MapPut("/teams/{id:guid}", async (Guid id, TeamRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var t = await ctx.Teams(user).Include(t => t.VirtualKeys).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new ApiFaultException(404, "Teamet finns inte.");
            var before = TeamAudit.Of(t);
            t.Name = input.Name.Trim(); t.Description = input.Description; t.IsActive = input.IsActive;
            await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.Team, id, before, TeamAudit.Of(t), InvalidationKind.Keys, ct);
            return TypedResults.Ok(TeamDto.Of(t));
        }).RequireAuthorization("manage").WithName("UpdateTeam");
        api.MapDelete("/teams/{id:guid}", async (Guid id, string? routingRules, AdminContext ctx, RoutingRuleService rules, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var t = await ctx.Teams(user).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new ApiFaultException(404, "Teamet finns inte.");
            if (await ctx.Db.VirtualKeys.AnyAsync(k => k.TeamId == id, ct)) { throw new ApiFaultException(409, "Teamet har nycklar och kan inte tas bort."); }
            await rules.ApplyScopeRemovalAsync(user, RoutingScope.Team, id, routingRules, "Teamet", ct);
            ctx.Db.Teams.Remove(t);
            await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.Team, id, TeamAudit.Of(t), null, InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization("manage").WithName("DeleteTeam")
            .WithSummary("Deletes a team without keys; routingRules=delete|deactivate says what happens to its routing rules");
    }
}
