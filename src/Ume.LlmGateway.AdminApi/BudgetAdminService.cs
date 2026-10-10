using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Budgets and their alerts. Users see and manage only budgets whose owner is in their departments.</summary>
public sealed class BudgetAdminService(AdminContext ctx, OwnerScopeResolver owners)
{
    public async Task<List<BudgetDto>> ListAsync(ClaimsPrincipal user, BudgetScope? scope, Guid? scopeId, CancellationToken ct)
    {
        var budgets = await ctx.Db.Budgets.AsNoTracking().Where(b => (scope == null || b.Scope == scope) && (scopeId == null || b.ScopeId == scopeId)).ToListAsync(ct);
        var names = await owners.NamesAsync(budgets.Select(b => Owner.Of(b.Scope, b.ScopeId)), user, ct);
        var visible = budgets.Where(b => names.ContainsKey(Owner.Of(b.Scope, b.ScopeId))).ToList();
        var spent = await SpentAsync(visible, ct);
        return [.. visible.Select(b => Dto(b, names[Owner.Of(b.Scope, b.ScopeId)], spent[b.Id]))];
    }

    public async Task<BudgetDto> CreateAsync(ClaimsPrincipal user, BudgetRequest input, CancellationToken ct)
    {
        var name = await OwnerNameAsync(user, input, ct);
        await EnsureUniquePeriodAsync(input, null, ct);
        var b = new Budget { CreatedAt = ctx.Now };
        Apply(b, input);
        ctx.Db.Budgets.Add(b);
        await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.Budget, b.Id, null, BudgetAudit.Of(b), InvalidationKind.Config, ct);
        return await DtoAsync(b, name, ct);
    }

    public async Task<BudgetDto> UpdateAsync(ClaimsPrincipal user, Guid id, BudgetRequest input, CancellationToken ct)
    {
        var b = await FindAsync(user, id, ct);
        var name = await OwnerNameAsync(user, input, ct);
        await EnsureUniquePeriodAsync(input, id, ct);
        var before = BudgetAudit.Of(b);
        Apply(b, input);
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.Budget, id, before, BudgetAudit.Of(b), InvalidationKind.Config, ct);
        return await DtoAsync(b, name, ct);
    }

    public async Task DeleteAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var b = await FindAsync(user, id, ct);
        ctx.Db.Budgets.Remove(b);
        await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.Budget, id, BudgetAudit.Of(b), null, InvalidationKind.Config, ct);
    }

    public async Task<List<AlertDto>> AlertsAsync(ClaimsPrincipal user, bool? acknowledged, CancellationToken ct)
    {
        var alerts = await ctx.Db.AlertEvents.AsNoTracking().Where(a => acknowledged == null || a.Acknowledged == acknowledged).OrderByDescending(a => a.Timestamp).ToListAsync(ct);
        var names = await owners.NamesAsync(alerts.Select(a => Owner.Of(a.Scope, a.ScopeId)), user, ct);
        return [.. alerts
            .Where(a => names.ContainsKey(Owner.Of(a.Scope, a.ScopeId)))
            .Select(a => new AlertDto(a.Id, a.BudgetId, a.Scope, names[Owner.Of(a.Scope, a.ScopeId)], a.ThresholdPercent, a.SpentSek, a.LimitSek, a.PeriodStart, a.Timestamp, a.Acknowledged))];
    }

    public async Task AcknowledgeAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var a = await ctx.Db.AlertEvents.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new ApiFaultException(404, "Larmet finns inte.");
        if (await owners.NameAsync(Owner.Of(a.Scope, a.ScopeId), user, ct) is null) { throw new ApiFaultException(404, "Larmet finns inte."); }
        a.Acknowledged = true;
        await ctx.SaveAsync(user, AuditActions.Acknowledge, AuditEntities.Alert, id, null, new AlertAudit(a.Acknowledged), InvalidationKind.Config, ct);
    }

    /// <summary>
    /// SEK spent against each budget in its current period, with one grouped query per owner kind and period. A key
    /// budget covers the key and every key it was rotated into, as in the gateway.
    /// </summary>
    public async Task<Dictionary<Guid, decimal>> SpentAsync(IReadOnlyCollection<Budget> budgets, CancellationToken ct)
    {
        var spent = budgets.ToDictionary(b => b.Id, _ => 0m);
        var rotations = budgets.Any(b => b.Scope == BudgetScope.VirtualKey) ? await ctx.Db.KeyRotationMapAsync(ct) : null;
        foreach (var group in budgets.GroupBy(b => (b.Scope, b.Period)))
        {
            var window = BudgetPeriods.GetWindow(group.Key.Period, ctx.Now);
            var usage = ctx.Db.UsageRecords.AsNoTracking().Where(u => u.Timestamp >= window.Start && u.Timestamp < window.End);
            switch (group.Key.Scope)
            {
                case BudgetScope.Department:
                {
                    var ids = group.Select(b => b.ScopeId).Distinct().ToArray();
                    var sums = await usage.Where(u => ids.Contains(u.DepartmentId)).GroupBy(u => u.DepartmentId)
                        .Select(g => new { g.Key, Sum = g.Sum(u => u.CostSek) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct);
                    foreach (var b in group) { spent[b.Id] = sums.GetValueOrDefault(b.ScopeId); }
                    break;
                }
                case BudgetScope.Team:
                {
                    var ids = group.Select(b => b.ScopeId).Distinct().ToArray();
                    var sums = await usage.Where(u => ids.Contains(u.TeamId)).GroupBy(u => u.TeamId)
                        .Select(g => new { g.Key, Sum = g.Sum(u => u.CostSek) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct);
                    foreach (var b in group) { spent[b.Id] = sums.GetValueOrDefault(b.ScopeId); }
                    break;
                }
                case BudgetScope.VirtualKey:
                {
                    var lineage = group.ToDictionary(b => b.Id, b => KeyRotation.Descendants(b.ScopeId, rotations!));
                    var ids = lineage.Values.SelectMany(k => k).Distinct().ToArray();
                    var sums = await usage.Where(u => ids.Contains(u.VirtualKeyId)).GroupBy(u => u.VirtualKeyId)
                        .Select(g => new { g.Key, Sum = g.Sum(u => u.CostSek) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct);
                    foreach (var b in group) { spent[b.Id] = lineage[b.Id].Sum(k => sums.GetValueOrDefault(k)); }
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(budgets), group.Key.Scope, "Unknown budget scope.");
            }
        }

        return spent;
    }

    private async Task<BudgetDto> DtoAsync(Budget b, string name, CancellationToken ct) =>
        Dto(b, name, await ctx.Db.SpentAsync(b, BudgetPeriods.GetWindow(b.Period, ctx.Now), ct));

    private BudgetDto Dto(Budget b, string name, decimal spent)
    {
        var window = BudgetPeriods.GetWindow(b.Period, ctx.Now);
        var percent = b.LimitSek == 0 ? (spent > 0 ? 100 : 0) : decimal.Round(spent / b.LimitSek * 100, 2);
        return new BudgetDto(b.Id, b.Scope, b.ScopeId, name, b.LimitSek, b.Period, b.AlertThresholds, b.IsActive, window.Start, window.End, spent, percent);
    }

    private static void Apply(Budget b, BudgetRequest input)
    {
        b.Scope = input.Scope; b.ScopeId = input.ScopeId; b.LimitSek = input.LimitSek; b.Period = input.Period;
        b.AlertThresholds = [.. input.AlertThresholds.Distinct().Order()]; b.IsActive = input.IsActive;
    }

    /// <summary>The budget, if its current owner is visible to the user.</summary>
    private async Task<Budget> FindAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var b = await ctx.Db.Budgets.SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw new ApiFaultException(404, "Budgeten finns inte.");
        return await owners.NameAsync(Owner.Of(b.Scope, b.ScopeId), user, ct) is null ? throw new ApiFaultException(404, "Budgeten finns inte.") : b;
    }

    private async Task<string> OwnerNameAsync(ClaimsPrincipal user, BudgetRequest input, CancellationToken ct) =>
        await owners.NameAsync(Owner.Of(input.Scope, input.ScopeId), user, ct) ?? throw new ApiFaultException(404, "Budgetens ägare finns inte.");

    /// <summary>One budget per owner and period: spend counters are keyed by owner and period, so two would share one counter.</summary>
    private async Task EnsureUniquePeriodAsync(BudgetRequest input, Guid? self, CancellationToken ct)
    {
        if (await ctx.Db.Budgets.AnyAsync(x => x.Scope == input.Scope && x.ScopeId == input.ScopeId && x.Period == input.Period && x.Id != self, ct))
        {
            throw new ApiFaultException(409, "Det finns redan en budget för samma period. Ändra den befintliga budgeten.");
        }
    }
}
