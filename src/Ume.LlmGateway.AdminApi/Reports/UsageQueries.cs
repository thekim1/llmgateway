using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Budgets;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Reports;

/// <summary>A usage record with the names of its key, team and department.</summary>
public sealed record UsageRow
{
    public required UsageRecord Record { get; init; }
    public required string KeyPrefix { get; init; }
    public required string KeyName { get; init; }
    public required string TeamName { get; init; }
    public required string DepartmentName { get; init; }
    public required string CostCenterCode { get; init; }
}

/// <summary>A usage record or a monthly total, in the shape the summary and the export add up (<see cref="UsageQueries.Facts"/>).</summary>
public sealed class UsageFact
{
    public Guid DepartmentId { get; init; }
    public Guid TeamId { get; init; }
    public Guid VirtualKeyId { get; init; }
    public required string RequestedModel { get; init; }
    public string? ProviderName { get; init; }
    public long Requests { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public long Errors { get; init; }
    public long Fallbacks { get; init; }
}

/// <summary>
/// Where a report period's usage comes from after data retention: records from <c>RecordsFrom</c>, and, when the period
/// reaches back before the oldest kept record, the monthly totals of months from <c>FirstMonth</c> (the month the period
/// starts in) up to <c>TotalsUntil</c>.
/// </summary>
internal readonly record struct RetainedRange(DateTimeOffset RecordsFrom, DateTimeOffset FirstMonth, DateTimeOffset? TotalsUntil);

/// <summary>Usage queries shared by the reports, in the user's departments (<see cref="AdminContext.Usage"/>).</summary>
internal static class UsageQueries
{
    public static IQueryable<UsageRow> WithNames(IQueryable<UsageRecord> query, GatewayDbContext db) =>
        from u in query
        join key in db.VirtualKeys on u.VirtualKeyId equals key.Id
        join team in db.Teams on u.TeamId equals team.Id
        join department in db.Departments on u.DepartmentId equals department.Id
        select new UsageRow { Record = u, KeyPrefix = key.Prefix, KeyName = key.Name, TeamName = team.Name, DepartmentName = department.Name, CostCenterCode = department.CostCenterCode };

    public static async Task<RetainedRange> RetainedAsync(AdminContext ctx, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var kept = await ctx.Db.UsageRetainedFromAsync(ct);
        if (kept is not { } from || from <= start)
        {
            return new RetainedRange(start, start, null);
        }

        return new RetainedRange(from, BudgetPeriods.GetWindow(BudgetPeriod.Monthly, start).Start, from < end ? from : end);
    }

    /// <summary>Records and, before the oldest kept record, monthly totals, in the user's departments (see <see cref="RetainedRange"/>).</summary>
    public static IQueryable<UsageFact> Facts(AdminContext ctx, RetainedRange range, DateTimeOffset end)
    {
        var records = ctx.Usage().AsNoTracking().Where(u => u.Timestamp >= range.RecordsFrom && u.Timestamp < end).Select(u => new UsageFact
        {
            DepartmentId = u.DepartmentId, TeamId = u.TeamId, VirtualKeyId = u.VirtualKeyId, RequestedModel = u.RequestedModel, ProviderName = u.ProviderName,
            Requests = 1, InputTokens = u.InputTokens, OutputTokens = u.OutputTokens, CostUsd = u.CostUsd,
            Errors = u.Outcome != RequestOutcome.Success ? 1 : 0, Fallbacks = u.FallbackCount,
        });
        if (range.TotalsUntil is not { } until)
        {
            return records;
        }

        return records.Concat(ctx.UsageTotals().Where(t => t.MonthStart >= range.FirstMonth && t.MonthStart < until).Select(t => new UsageFact
        {
            DepartmentId = t.DepartmentId, TeamId = t.TeamId, VirtualKeyId = t.VirtualKeyId, RequestedModel = t.RequestedModel, ProviderName = t.ProviderName,
            Requests = t.Requests, InputTokens = t.InputTokens, OutputTokens = t.OutputTokens, CostUsd = t.CostUsd,
            Errors = t.Requests - t.SuccessfulRequests, Fallbacks = t.Fallbacks,
        }));
    }

    // Member initialisers, not a constructor: EF translates GroupBy over the properties only then.
    public sealed class UsageFactRow
    {
        public required UsageFact Fact { get; init; }
        public required string KeyName { get; init; }
        public required string TeamName { get; init; }
        public required string DepartmentName { get; init; }
        public required string CostCenterCode { get; init; }
    }

    public static IQueryable<UsageFactRow> FactsWithNames(IQueryable<UsageFact> facts, GatewayDbContext db) =>
        from f in facts
        join key in db.VirtualKeys on f.VirtualKeyId equals key.Id
        join team in db.Teams on f.TeamId equals team.Id
        join department in db.Departments on f.DepartmentId equals department.Id
        select new UsageFactRow { Fact = f, KeyName = key.Name, TeamName = team.Name, DepartmentName = department.Name, CostCenterCode = department.CostCenterCode };
}
