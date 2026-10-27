using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
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
    bool IsActive = true,
    // Applies to the team's keys. Omitted = unchanged; {} clears it.
    RequestPolicy? RequestPolicy = null,
    // Matched against team admins' teamCodes claim. Omitted = unchanged; "" clears it.
    [property: StringLength(100)] string? Code = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var message in RequestPolicy?.Validate() ?? [])
        {
            yield return new ValidationResult(message, [nameof(RequestPolicy)]);
        }
        if (Code is not null && Code.Trim().Any(char.IsControl))
        {
            yield return new ValidationResult("The team code cannot contain control characters.", [nameof(Code)]);
        }
    }
}

public static class OrganisationEndpoints
{
    public static void MapOrganisation(this RouteGroupBuilder root)
    {
        // Team admins list their own teams (to pick one for a key); everything else here needs a role that reads.
        root.MapGet("/teams", async (Guid? departmentId, AdminContext ctx, CancellationToken ct) =>
        {
            var query = ctx.VisibleTeams().Include(t => t.VirtualKeys).AsQueryable();
            if (departmentId is { } id) { query = query.Where(t => t.DepartmentId == id); }
            return TypedResults.Ok((await query.OrderBy(t => t.Name).ToListAsync(ct)).Select(TeamDto.Of).ToList());
        }).RequireAuthorization(AdminPolicies.ListTeams).WithTags("Organisation").WithName("ListTeams");
        var api = root.MapGroup("").RequireAuthorization(AdminPolicies.Read).WithTags("Organisation");
        api.MapGet("/departments", async (AdminContext ctx, CancellationToken ct) =>
            TypedResults.Ok((await ctx.Departments().Include(d => d.Teams).OrderBy(d => d.Name).ToListAsync(ct)).Select(DepartmentDto.Of).ToList()))
            .WithName("ListDepartments");
        api.MapPost("/departments", async (DepartmentRequest input, AdminContext ctx, CancellationToken ct) =>
        {
            var d = new Department { Name = input.Name.Trim(), CostCenterCode = input.CostCenterCode.Trim(), CreatedAt = ctx.Now };
            ctx.Db.Departments.Add(d);
            await ctx.SaveAsync(AuditActions.Create, AuditEntities.Department, d.Id, null, DepartmentAudit.Of(d), InvalidationKind.Config, ct);
            return TypedResults.Created($"/api/departments/{d.Id}", DepartmentDto.Of(d));
        }).RequireAuthorization(AdminPolicies.Admin).WithName("CreateDepartment");
        api.MapPut("/departments/{id:guid}", async (Guid id, DepartmentRequest input, AdminContext ctx, CancellationToken ct) =>
        {
            var d = await ctx.Db.Departments.Include(d => d.Teams).SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new ApiFaultException(404, "The department does not exist.");
            var before = DepartmentAudit.Of(d);
            d.Name = input.Name.Trim(); d.CostCenterCode = input.CostCenterCode.Trim(); d.IsActive = input.IsActive;
            // Keys too: an authenticated key carries its department's name and state.
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Department, id, before, DepartmentAudit.Of(d), [InvalidationKind.Config, InvalidationKind.Keys], ct);
            return TypedResults.Ok(DepartmentDto.Of(d));
        }).RequireAuthorization(AdminPolicies.Admin).WithName("UpdateDepartment");
        api.MapDelete("/departments/{id:guid}", async (Guid id, string? routingRules, AdminContext ctx, RoutingRuleService rules, CancellationToken ct) =>
        {
            var d = await ctx.Db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new ApiFaultException(404, "The department does not exist.");
            if (await ctx.Db.Teams.AnyAsync(t => t.DepartmentId == id, ct)) { throw new ApiFaultException(409, "The department has teams and cannot be deleted."); }
            await rules.ApplyScopeRemovalAsync(RoutingScope.Department, id, routingRules, "The department", ct);
            ctx.Db.Departments.Remove(d);
            await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Department, id, DepartmentAudit.Of(d), null, InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Admin).WithName("DeleteDepartment")
            .WithSummary("Deletes an empty department; routingRules=delete|deactivate says what happens to its routing rules");
        api.MapPost("/teams", async (TeamRequest input, AdminContext ctx, CancellationToken ct) =>
        {
            var d = await ctx.Departments().SingleOrDefaultAsync(d => d.Id == input.DepartmentId, ct) ?? throw new ApiFaultException(404, "The department does not exist.");
            var t = new Team
            {
                Department = d, DepartmentId = d.Id, Name = input.Name.Trim(), Description = input.Description, CreatedAt = ctx.Now,
                RequestPolicy = input.RequestPolicy is { IsEmpty: false } policy ? policy : null,
                Code = await UniqueCodeAsync(input.Code, null, ctx, ct),
            };
            ctx.Db.Teams.Add(t);
            await ctx.SaveAsync(AuditActions.Create, AuditEntities.Team, t.Id, null, TeamAudit.Of(t), InvalidationKind.Config, ct);
            return TypedResults.Created($"/api/teams/{t.Id}", TeamDto.Of(t));
        }).RequireAuthorization(AdminPolicies.Manage).WithName("CreateTeam");
        api.MapPut("/teams/{id:guid}", async (Guid id, TeamRequest input, AdminContext ctx, CancellationToken ct) =>
        {
            var t = await ctx.Teams().Include(t => t.VirtualKeys).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new ApiFaultException(404, "The team does not exist.");
            var before = TeamAudit.Of(t);
            t.Name = input.Name.Trim(); t.Description = input.Description; t.IsActive = input.IsActive;
            if (input.RequestPolicy is { } policy) { t.RequestPolicy = policy.IsEmpty ? null : policy; }
            if (input.Code is not null) { t.Code = await UniqueCodeAsync(input.Code, id, ctx, ct); }
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Team, id, before, TeamAudit.Of(t), InvalidationKind.Keys, ct);
            return TypedResults.Ok(TeamDto.Of(t));
        }).RequireAuthorization(AdminPolicies.Manage).WithName("UpdateTeam");
        api.MapDelete("/teams/{id:guid}", async (Guid id, string? routingRules, AdminContext ctx, RoutingRuleService rules, CancellationToken ct) =>
        {
            var t = await ctx.Teams().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new ApiFaultException(404, "The team does not exist.");
            if (await ctx.Db.VirtualKeys.AnyAsync(k => k.TeamId == id, ct)) { throw new ApiFaultException(409, "The team has keys and cannot be deleted."); }
            await rules.ApplyScopeRemovalAsync(RoutingScope.Team, id, routingRules, "The team", ct);
            ctx.Db.Teams.Remove(t);
            await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Team, id, TeamAudit.Of(t), null, InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Manage).WithName("DeleteTeam")
            .WithSummary("Deletes a team without keys; routingRules=delete|deactivate says what happens to its routing rules");
    }

    /// <summary>The trimmed code, null when empty; 409 when another team (in any department) already has it.</summary>
    private static async Task<string?> UniqueCodeAsync(string? code, Guid? teamId, AdminContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (await ctx.Db.Teams.AnyAsync(t => t.Code == trimmed && t.Id != teamId, ct))
        {
            throw new ApiFaultException(409, "Another team already has this team code.");
        }
        return trimmed;
    }
}
