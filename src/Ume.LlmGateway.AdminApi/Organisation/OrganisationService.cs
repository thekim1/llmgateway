using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.RoutingRules;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Organisation;

/// <summary>Departments and teams. Gateway admins manage departments; department admins manage the teams of theirs.</summary>
public sealed class OrganisationService(AdminContext ctx, RoutingRuleService rules)
{
    public async Task<List<DepartmentDto>> DepartmentsAsync(CancellationToken ct) =>
        [.. (await ctx.Departments().Include(d => d.Teams).OrderBy(d => d.Name).ToListAsync(ct)).Select(DepartmentDto.Of)];

    public async Task<DepartmentDto> CreateDepartmentAsync(DepartmentRequest input, CancellationToken ct)
    {
        var d = new Department { Name = input.Name.Trim(), CostCenterCode = input.CostCenterCode.Trim(), CreatedAt = ctx.Now };
        ctx.Db.Departments.Add(d);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Department, d.Id, null, DepartmentAudit.Of(d), InvalidationKind.Config, ct);
        return DepartmentDto.Of(d);
    }

    public async Task<DepartmentDto> UpdateDepartmentAsync(Guid id, DepartmentRequest input, CancellationToken ct)
    {
        var d = await ctx.Db.Departments.Include(d => d.Teams).SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw DepartmentNotFound();
        var before = DepartmentAudit.Of(d);
        d.Name = input.Name.Trim(); d.CostCenterCode = input.CostCenterCode.Trim(); d.IsActive = input.IsActive;
        // Keys too: an authenticated key carries its department's name and state.
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Department, id, before, DepartmentAudit.Of(d), [InvalidationKind.Config, InvalidationKind.Keys], ct);
        return DepartmentDto.Of(d);
    }

    /// <summary>Deletes an empty department; <paramref name="routingRules"/> says what happens to its routing rules.</summary>
    public async Task DeleteDepartmentAsync(Guid id, string? routingRules, CancellationToken ct)
    {
        var d = await ctx.Db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw DepartmentNotFound();
        if (await ctx.Db.Teams.AnyAsync(t => t.DepartmentId == id, ct)) { throw new ApiFaultException(409, "The department has teams and cannot be deleted."); }
        await rules.ApplyScopeRemovalAsync(RoutingScope.Department, id, routingRules, "The department", ct);
        ctx.Db.Departments.Remove(d);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Department, id, DepartmentAudit.Of(d), null, InvalidationKind.Config, ct);
    }

    /// <summary>The teams the user may list (<see cref="AdminContext.VisibleTeams"/>), optionally of one department.</summary>
    public async Task<List<TeamDto>> TeamsAsync(Guid? departmentId, CancellationToken ct)
    {
        var query = ctx.VisibleTeams().Include(t => t.VirtualKeys).AsQueryable();
        if (departmentId is { } id) { query = query.Where(t => t.DepartmentId == id); }
        return [.. (await query.OrderBy(t => t.Name).ToListAsync(ct)).Select(TeamDto.Of)];
    }

    public async Task<TeamDto> CreateTeamAsync(TeamRequest input, CancellationToken ct)
    {
        var d = await ctx.Departments().SingleOrDefaultAsync(d => d.Id == input.DepartmentId, ct) ?? throw DepartmentNotFound();
        var t = new Team
        {
            Department = d, DepartmentId = d.Id, Name = input.Name.Trim(), Description = input.Description, CreatedAt = ctx.Now,
            RequestPolicy = input.RequestPolicy is { IsEmpty: false } policy ? policy : null,
            Code = await UniqueCodeAsync(input.Code, null, ct),
        };
        ctx.Db.Teams.Add(t);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Team, t.Id, null, TeamAudit.Of(t), InvalidationKind.Config, ct);
        return TeamDto.Of(t);
    }

    public async Task<TeamDto> UpdateTeamAsync(Guid id, TeamRequest input, CancellationToken ct)
    {
        var t = await ctx.Teams().Include(t => t.VirtualKeys).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw TeamNotFound();
        var before = TeamAudit.Of(t);
        t.Name = input.Name.Trim(); t.Description = input.Description; t.IsActive = input.IsActive;
        if (input.RequestPolicy is { } policy) { t.RequestPolicy = policy.IsEmpty ? null : policy; }
        if (input.Code is not null) { t.Code = await UniqueCodeAsync(input.Code, id, ct); }
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Team, id, before, TeamAudit.Of(t), InvalidationKind.Keys, ct);
        return TeamDto.Of(t);
    }

    /// <summary>Deletes a team without keys; <paramref name="routingRules"/> says what happens to its routing rules.</summary>
    public async Task DeleteTeamAsync(Guid id, string? routingRules, CancellationToken ct)
    {
        var t = await ctx.Teams().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw TeamNotFound();
        if (await ctx.Db.VirtualKeys.AnyAsync(k => k.TeamId == id, ct)) { throw new ApiFaultException(409, "The team has keys and cannot be deleted."); }
        await rules.ApplyScopeRemovalAsync(RoutingScope.Team, id, routingRules, "The team", ct);
        ctx.Db.Teams.Remove(t);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Team, id, TeamAudit.Of(t), null, InvalidationKind.Config, ct);
    }

    /// <summary>The trimmed code, null when empty; 409 when another team (in any department) already has it.</summary>
    private async Task<string?> UniqueCodeAsync(string? code, Guid? teamId, CancellationToken ct)
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

    private static ApiFaultException DepartmentNotFound() => new(404, "The department does not exist.");
    private static ApiFaultException TeamNotFound() => new(404, "The team does not exist.");
}
