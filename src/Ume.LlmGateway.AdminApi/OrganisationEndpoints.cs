using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
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
    public static object DepartmentDto(Department d) => new { d.Id, d.Name, d.CostCenterCode, d.IsActive, teamCount = d.Teams.Count, d.CreatedAt };
    public static object TeamDto(Team t) => new { t.Id, t.DepartmentId, departmentName = t.Department?.Name, t.Name, t.Description, t.IsActive, keyCount = t.VirtualKeys.Count, t.CreatedAt };

    public static void MapOrganisation(this RouteGroupBuilder api)
    {
        api = api.MapGroup("").RequireAuthorization("read");
        api.MapGet("/departments", async (AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
            (await ctx.Departments(user).Include(d => d.Teams).OrderBy(d => d.Name).ToListAsync(ct)).Select(DepartmentDto));
        api.MapPost("/departments", async (DepartmentRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = new Department { Name = input.Name.Trim(), CostCenterCode = input.CostCenterCode.Trim(), CreatedAt = ctx.Now };
            ctx.Db.Departments.Add(d);
            await ctx.SaveAsync(user, "create", "Department", d.Id, null, DepartmentDto(d), InvalidationKind.Config, ct);
            return Results.Created($"/api/departments/{d.Id}", DepartmentDto(d));
        }).RequireAuthorization("admin");
        api.MapPut("/departments/{id:guid}", async (Guid id, DepartmentRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = await ctx.Db.Departments.Include(d => d.Teams).SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new AdminFaultException(404, "Förvaltningen finns inte.");
            var before = DepartmentDto(d);
            d.Name = input.Name.Trim(); d.CostCenterCode = input.CostCenterCode.Trim(); d.IsActive = input.IsActive;
            await ctx.SaveAsync(user, "update", "Department", id, before, DepartmentDto(d), InvalidationKind.Config, ct);
            await ctx.SaveAsync(user, "invalidate", "Keys", id, null, null, InvalidationKind.Keys, ct);
            return Results.Ok(DepartmentDto(d));
        }).RequireAuthorization("admin");
        api.MapDelete("/departments/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = await ctx.Db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new AdminFaultException(404, "Förvaltningen finns inte.");
            if (await ctx.Db.Teams.AnyAsync(t => t.DepartmentId == id, ct)) { throw new AdminFaultException(409, "Förvaltningen har team och kan inte tas bort."); }
            ctx.Db.Departments.Remove(d);
            await ctx.SaveAsync(user, "delete", "Department", id, DepartmentDto(d), null, InvalidationKind.Config, ct);
            return Results.NoContent();
        }).RequireAuthorization("admin");
        api.MapGet("/teams", async (Guid? departmentId, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var query = ctx.Teams(user).Include(t => t.VirtualKeys).AsQueryable();
            if (departmentId is { } id) { query = query.Where(t => t.DepartmentId == id); }
            return (await query.OrderBy(t => t.Name).ToListAsync(ct)).Select(TeamDto);
        });
        api.MapPost("/teams", async (TeamRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var d = await ctx.Departments(user).SingleOrDefaultAsync(d => d.Id == input.DepartmentId, ct) ?? throw new AdminFaultException(404, "Förvaltningen finns inte.");
            var t = new Team { Department = d, DepartmentId = d.Id, Name = input.Name.Trim(), Description = input.Description, CreatedAt = ctx.Now };
            ctx.Db.Teams.Add(t);
            await ctx.SaveAsync(user, "create", "Team", t.Id, null, TeamDto(t), InvalidationKind.Config, ct);
            return Results.Created($"/api/teams/{t.Id}", TeamDto(t));
        }).RequireAuthorization("manage");
        api.MapPut("/teams/{id:guid}", async (Guid id, TeamRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var t = await ctx.Teams(user).Include(t => t.VirtualKeys).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new AdminFaultException(404, "Teamet finns inte.");
            var before = TeamDto(t);
            t.Name = input.Name.Trim(); t.Description = input.Description; t.IsActive = input.IsActive;
            await ctx.SaveAsync(user, "update", "Team", id, before, TeamDto(t), InvalidationKind.Keys, ct);
            return Results.Ok(TeamDto(t));
        }).RequireAuthorization("manage");
        api.MapDelete("/teams/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var t = await ctx.Teams(user).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new AdminFaultException(404, "Teamet finns inte.");
            if (await ctx.Db.VirtualKeys.AnyAsync(k => k.TeamId == id, ct)) { throw new AdminFaultException(409, "Teamet har nycklar och kan inte tas bort."); }
            ctx.Db.Teams.Remove(t);
            await ctx.SaveAsync(user, "delete", "Team", id, TeamDto(t), null, InvalidationKind.Config, ct);
            return Results.NoContent();
        }).RequireAuthorization("manage");
    }
}
