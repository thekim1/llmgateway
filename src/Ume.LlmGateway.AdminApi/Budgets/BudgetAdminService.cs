using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Budgets;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Budgets;

/// <summary>Budgets and their alerts. Users see and manage only budgets whose owner is in their departments.</summary>
public sealed class BudgetAdminService(AdminContext ctx, OwnerScopeResolver owners)
{
    public async Task<List<BudgetDto>> ListAsync(BudgetScope? scope, Guid? scopeId, CancellationToken ct)
    {
        var budgets = await ctx.Db.Budgets.AsNoTracking().Where(b => (scope == null || b.Scope == scope) && (scopeId == null || b.ScopeId == scopeId)).ToListAsync(ct);
        var names = await owners.NamesAsync(budgets.Select(b => Owner.Of(b.Scope, b.ScopeId)), ct);
        var visible = budgets.Where(b => names.ContainsKey(Owner.Of(b.Scope, b.ScopeId))).ToList();
        var spent = await SpentAsync(visible, ct);
        return [.. visible.Select(b => Dto(b, names[Owner.Of(b.Scope, b.ScopeId)], spent[b.Id]))];
    }

    public async Task<BudgetDto> CreateAsync(BudgetRequest input, CancellationToken ct)
    {
        var name = await OwnerNameAsync(input, ct);
        await EnsureUniquePeriodAsync(input, null, ct);
        var b = new Budget { CreatedAt = ctx.Now };
        Apply(b, input);
        ctx.Db.Budgets.Add(b);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Budget, b.Id, null, BudgetAudit.Of(b), InvalidationKind.Config, ct);
        return await DtoAsync(b, name, ct);
    }

    public async Task<BudgetDto> UpdateAsync(Guid id, BudgetRequest input, CancellationToken ct)
    {
        var b = await FindAsync(id, ct);
        var name = await OwnerNameAsync(input, ct);
        await EnsureUniquePeriodAsync(input, id, ct);
        var before = BudgetAudit.Of(b);
        Apply(b, input);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Budget, id, before, BudgetAudit.Of(b), InvalidationKind.Config, ct);
        return await DtoAsync(b, name, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var b = await FindAsync(id, ct);
        ctx.Db.Budgets.Remove(b);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Budget, id, BudgetAudit.Of(b), null, InvalidationKind.Config, ct);
    }

    public async Task<List<AlertDto>> AlertsAsync(bool? acknowledged, CancellationToken ct)
    {
        var alerts = await ctx.Db.AlertEvents.AsNoTracking().Where(a => acknowledged == null || a.Acknowledged == acknowledged).OrderByDescending(a => a.Timestamp).ToListAsync(ct);
        var names = await owners.NamesAsync(alerts.Select(a => Owner.Of(a.Scope, a.ScopeId)), ct);
        return [.. alerts
            .Where(a => names.ContainsKey(Owner.Of(a.Scope, a.ScopeId)))
            .Select(a => new AlertDto(a.Id, a.BudgetId, a.Scope, names[Owner.Of(a.Scope, a.ScopeId)], a.ThresholdPercent, a.SpentUsd, a.LimitUsd, a.PeriodStart,
                a.Timestamp, a.Acknowledged, a.SpentTokens, a.LimitTokens))];
    }

    public async Task AcknowledgeAsync(Guid id, CancellationToken ct)
    {
        var a = await ctx.Db.AlertEvents.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new ApiFaultException(404, "The alert does not exist.");
        if (await owners.NameAsync(Owner.Of(a.Scope, a.ScopeId), ct) is null) { throw new ApiFaultException(404, "The alert does not exist."); }
        a.Acknowledged = true;
        await ctx.SaveAsync(AuditActions.Acknowledge, AuditEntities.Alert, id, null, new AlertAudit(a.Acknowledged), InvalidationKind.Config, ct);
    }

    /// <summary>
    /// What each budget used in its current period, in its unit: USD with one grouped query per owner kind and period, and
    /// tokens with one query per token quota (they are few). A key budget covers the key and every key it was rotated
    /// into, as in the gateway.
    /// </summary>
    public async Task<Dictionary<Guid, decimal>> SpentAsync(IReadOnlyCollection<Budget> budgets, CancellationToken ct)
    {
        var spent = budgets.ToDictionary(b => b.Id, _ => 0m);
        var rotations = budgets.Any(b => b.Scope == BudgetScope.VirtualKey) ? await ctx.Db.KeyRotationMapAsync(ct) : null;
        var retainedFrom = budgets.Count > 0 ? await ctx.Db.UsageRetainedFromAsync(ct) : null;
        foreach (var quota in budgets.Where(b => b.Unit == BudgetUnit.Tokens))
        {
            spent[quota.Id] = await ctx.Db.SpentAsync(quota, BudgetPeriods.GetWindow(quota.Period, ctx.Now), ct, rotations);
        }

        foreach (var group in budgets.Where(b => b.Unit != BudgetUnit.Tokens).GroupBy(b => (b.Scope, b.Period)))
        {
            var window = BudgetPeriods.GetWindow(group.Key.Period, ctx.Now);
            var usage = ctx.Db.CostsIn(window, retainedFrom);
            switch (group.Key.Scope)
            {
                case BudgetScope.Department:
                {
                    var ids = group.Select(b => b.ScopeId).Distinct().ToArray();
                    var sums = await usage.Where(u => ids.Contains(u.DepartmentId)).GroupBy(u => u.DepartmentId)
                        .Select(g => new { g.Key, Sum = g.Sum(u => u.CostUsd) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct);
                    foreach (var b in group) { spent[b.Id] = sums.GetValueOrDefault(b.ScopeId); }
                    break;
                }
                case BudgetScope.Team:
                {
                    var ids = group.Select(b => b.ScopeId).Distinct().ToArray();
                    var sums = await usage.Where(u => ids.Contains(u.TeamId)).GroupBy(u => u.TeamId)
                        .Select(g => new { g.Key, Sum = g.Sum(u => u.CostUsd) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct);
                    foreach (var b in group) { spent[b.Id] = sums.GetValueOrDefault(b.ScopeId); }
                    break;
                }
                case BudgetScope.VirtualKey:
                {
                    var lineage = group.ToDictionary(b => b.Id, b => KeyRotation.Descendants(b.ScopeId, rotations!));
                    var ids = lineage.Values.SelectMany(k => k).Distinct().ToArray();
                    var sums = await usage.Where(u => ids.Contains(u.VirtualKeyId)).GroupBy(u => u.VirtualKeyId)
                        .Select(g => new { g.Key, Sum = g.Sum(u => u.CostUsd) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct);
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
        var percent = b.Limit == 0 ? (spent > 0 ? 100 : 0) : decimal.Round(spent / b.Limit * 100, 2);
        var tokens = b.Unit == BudgetUnit.Tokens;
        return new BudgetDto(b.Id, b.Scope, b.ScopeId, name, b.LimitUsd, b.Period, b.AlertThresholds, b.IsActive, window.Start, window.End,
            tokens ? 0 : spent, percent, b.Unit, b.LimitTokens, tokens ? (long)spent : 0, b.Residency);
    }

    private static void Apply(Budget b, BudgetRequest input)
    {
        var tokens = input.Unit == BudgetUnit.Tokens;
        b.Scope = input.Scope; b.ScopeId = input.ScopeId; b.Period = input.Period; b.Unit = input.Unit;
        b.LimitUsd = tokens ? 0 : input.LimitUsd; b.LimitTokens = tokens ? input.LimitTokens : 0; b.Residency = tokens ? input.Residency : null;
        b.AlertThresholds = [.. input.AlertThresholds.Distinct().Order()]; b.IsActive = input.IsActive;
    }

    /// <summary>The budget, if its current owner is visible to the user.</summary>
    private async Task<Budget> FindAsync(Guid id, CancellationToken ct)
    {
        var b = await ctx.Db.Budgets.SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw new ApiFaultException(404, "The budget does not exist.");
        return await owners.NameAsync(Owner.Of(b.Scope, b.ScopeId), ct) is null ? throw new ApiFaultException(404, "The budget does not exist.") : b;
    }

    private async Task<string> OwnerNameAsync(BudgetRequest input, CancellationToken ct) =>
        await owners.NameAsync(Owner.Of(input.Scope, input.ScopeId), ct) ?? throw new ApiFaultException(404, "The budget's owner does not exist.");

    /// <summary>
    /// One cost budget per owner and period, and one token quota per owner, period and residency: counters are keyed that
    /// way, so two would share one counter.
    /// </summary>
    private async Task EnsureUniquePeriodAsync(BudgetRequest input, Guid? self, CancellationToken ct)
    {
        var residency = input.Unit == BudgetUnit.Tokens ? input.Residency : null;
        if (await ctx.Db.Budgets.AnyAsync(x => x.Scope == input.Scope && x.ScopeId == input.ScopeId && x.Period == input.Period && x.Id != self
                && x.Unit == input.Unit && x.Residency == residency, ct))
        {
            throw new ApiFaultException(409, input.Unit == BudgetUnit.Tokens
                ? "There is already a token quota for the same period and residency. Change the existing one."
                : "There is already a budget for the same period. Change the existing budget.");
        }
    }
}
