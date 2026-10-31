using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.AdminApi.Reports;

/// <summary>The filters of <c>/api/usage/requests</c>.</summary>
public sealed record UsageRequestQuery(
    DateTimeOffset? From, DateTimeOffset? To, Guid? KeyId, Guid? TeamId, Guid? DepartmentId, string? Model, string? Provider, RequestOutcome? Outcome,
    bool? Errors, int? Page, int? PageSize);

/// <summary>The <c>groupBy</c> of <c>/api/usage/summary</c> (case-insensitive; default <c>department</c>).</summary>
public enum UsageGrouping
{
    Department,
    Team,
    Key,
    Model,
    Provider,
    Day,
}

/// <summary>Usage requests, totals and the cost export in the user's departments, and the audit log.</summary>
public sealed class ReportService(AdminContext ctx)
{
    private const char CsvDelimiter = ';';

    public async Task<PageDto<UsageRequestDto>> RequestsAsync(UsageRequestQuery q, CancellationToken ct)
    {
        var (skip, take) = ReportQuery.Pagination(q.Page, q.PageSize);
        var (start, end) = ReportQuery.Range(q.From, q.To, ctx.Now);
        var query = ctx.Usage().Where(u => u.Timestamp >= start && u.Timestamp < end);
        if (q.KeyId is { } key) { query = query.Where(u => u.VirtualKeyId == key); }
        if (q.TeamId is { } team) { query = query.Where(u => u.TeamId == team); }
        if (q.DepartmentId is { } department) { query = query.Where(u => u.DepartmentId == department); }
        if (!string.IsNullOrWhiteSpace(q.Model)) { var name = q.Model.Trim(); query = query.Where(u => u.RequestedModel == name); }
        // "-" is how the summary groups requests no provider handled (refused by the gateway first).
        if (q.Provider?.Trim() is "-") { query = query.Where(u => u.ProviderName == null); }
        else if (!string.IsNullOrWhiteSpace(q.Provider)) { var name = q.Provider.Trim(); query = query.Where(u => u.ProviderName == name); }
        if (q.Outcome is { } result) { query = query.Where(u => u.Outcome == result); }
        if (q.Errors == true) { query = query.Where(u => u.Outcome != RequestOutcome.Success); }
        var total = await query.CountAsync(ct);
        var rows = await UsageQueries.WithNames(query.OrderByDescending(u => u.Timestamp).ThenByDescending(u => u.Id).Skip(skip).Take(take), ctx.Db).ToListAsync(ct);
        return new PageDto<UsageRequestDto>([.. rows.Select(UsageDto)], total);
    }

    public async Task<UsageRequestDto> RequestAsync(string requestId, CancellationToken ct)
    {
        if (requestId.Length > 64) { throw new ApiFaultException(400, "The request id is too long."); }
        var row = await UsageQueries.WithNames(ctx.Usage().Where(u => u.RequestId == requestId), ctx.Db).FirstOrDefaultAsync(ct)
            ?? throw new ApiFaultException(404, "The request does not exist.");
        return UsageDto(row);
    }

    public async Task<UsageSummaryDto> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, string? groupBy, Guid? departmentId, Guid? teamId, CancellationToken ct)
    {
        var grouping = groupBy is null ? UsageGrouping.Department
            : EnumQuery.TryParse<UsageGrouping>(groupBy, out var parsed) ? parsed
            : throw new ApiFaultException(400, "The grouping is invalid.");
        var (start, end) = ReportQuery.Range(from, to, ctx.Now);
        var retained = await UsageQueries.RetainedAsync(ctx, start, end, ct);
        var rows = grouping == UsageGrouping.Day
            ? await SummaryByDayAsync(retained, end, departmentId, teamId, ct)
            : await SummaryByOwnerAsync(grouping, retained, end, departmentId, teamId, ct);
        return new UsageSummaryDto(start, end, rows.Sum(r => r.CostUsd), rows.Sum(r => r.Requests),
            rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens), rows.Sum(r => r.Errors), rows, retained.TotalsUntil is null ? null : retained.RecordsFrom);
    }

    /// <summary>By day from records; a month that only has totals is one row labelled yyyy-MM.</summary>
    private async Task<List<UsageSummaryRow>> SummaryByDayAsync(RetainedRange retained, DateTimeOffset end, Guid? departmentId, Guid? teamId, CancellationToken ct)
    {
        var query = ctx.Usage().Where(u => u.Timestamp >= retained.RecordsFrom && u.Timestamp < end);
        if (teamId is { } team) { query = query.Where(u => u.TeamId == team); }
        if (departmentId is { } department) { query = query.Where(u => u.DepartmentId == department); }
#pragma warning disable CA1305 // translated to SQL (a text cast in the database), no .NET culture is involved
        var rows = await query.GroupBy(u => u.Timestamp.Date.ToString()).Select(g => new UsageSummaryRow(g.Key, g.Key,
#pragma warning restore CA1305
            g.LongCount(), g.Sum(u => u.InputTokens), g.Sum(u => u.OutputTokens),
            g.Sum(u => u.CostUsd), g.LongCount(u => u.Outcome != RequestOutcome.Success), g.Sum(u => (long)u.FallbackCount))).ToListAsync(ct);
        if (retained.TotalsUntil is { } until)
        {
            var totals = ctx.UsageTotals().Where(t => t.MonthStart >= retained.FirstMonth && t.MonthStart < until);
            if (teamId is { } t1) { totals = totals.Where(t => t.TeamId == t1); }
            if (departmentId is { } d1) { totals = totals.Where(t => t.DepartmentId == d1); }
            var months = await totals.GroupBy(t => t.MonthStart).Select(g => new
            {
                g.Key, Requests = g.Sum(t => t.Requests), Input = g.Sum(t => t.InputTokens), Output = g.Sum(t => t.OutputTokens),
                Cost = g.Sum(t => t.CostUsd), Errors = g.Sum(t => t.Requests - t.SuccessfulRequests), Fallbacks = g.Sum(t => t.Fallbacks),
            }).ToListAsync(ct);
            rows.InsertRange(0, months.OrderBy(m => m.Key).Select(m =>
            {
                var label = TimeZoneInfo.ConvertTime(m.Key, BudgetPeriods.Stockholm).ToString("yyyy-MM", CultureInfo.InvariantCulture);
                return new UsageSummaryRow(label, label, m.Requests, m.Input, m.Output, m.Cost, m.Errors, m.Fallbacks);
            }));
        }
        return rows;
    }

    private async Task<List<UsageSummaryRow>> SummaryByOwnerAsync(UsageGrouping grouping, RetainedRange retained, DateTimeOffset end, Guid? departmentId, Guid? teamId,
        CancellationToken ct)
    {
        var facts = UsageQueries.Facts(ctx, retained, end);
        if (teamId is { } team) { facts = facts.Where(f => f.TeamId == team); }
        if (departmentId is { } department) { facts = facts.Where(f => f.DepartmentId == department); }
        var names = UsageQueries.FactsWithNames(facts, ctx.Db);
        var groups = grouping switch
        {
            UsageGrouping.Department => names.GroupBy(u => new { Key = u.Fact.DepartmentId.ToString(), Label = u.DepartmentName }),
            UsageGrouping.Team => names.GroupBy(u => new { Key = u.Fact.TeamId.ToString(), Label = u.TeamName }),
            UsageGrouping.Key => names.GroupBy(u => new { Key = u.Fact.VirtualKeyId.ToString(), Label = u.KeyName }),
            UsageGrouping.Model => names.GroupBy(u => new { Key = u.Fact.RequestedModel, Label = u.Fact.RequestedModel }),
            UsageGrouping.Provider => names.GroupBy(u => new { Key = u.Fact.ProviderName ?? "-", Label = u.Fact.ProviderName ?? "-" }),
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "Unknown grouping."),
        };
        return await groups.Select(g => new UsageSummaryRow(g.Key.Key, g.Key.Label,
            g.Sum(u => u.Fact.Requests), g.Sum(u => u.Fact.InputTokens), g.Sum(u => u.Fact.OutputTokens),
            g.Sum(u => u.Fact.CostUsd), g.Sum(u => u.Fact.Errors), g.Sum(u => u.Fact.Fallbacks))).ToListAsync(ct);
    }

    /// <summary>Cost per cost centre, department, team and key as UTF-8 CSV with a BOM (semicolon-separated, Swedish decimals); audited.</summary>
    public async Task<byte[]> ExportCsvAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (start, end) = ReportQuery.Range(from, to, ctx.Now);
        var query = UsageQueries.FactsWithNames(UsageQueries.Facts(ctx, await UsageQueries.RetainedAsync(ctx, start, end, ct), end), ctx.Db);
        var rows = await query.GroupBy(u => new { u.CostCenterCode, u.DepartmentName, u.TeamName, u.KeyName })
            .Select(g => new { g.Key.CostCenterCode, g.Key.DepartmentName, g.Key.TeamName, g.Key.KeyName,
                Requests = g.Sum(u => u.Fact.Requests), Tokens = g.Sum(u => u.Fact.InputTokens + u.Fact.OutputTokens), Cost = g.Sum(u => u.Fact.CostUsd) }).ToListAsync(ct);
        var csv = new StringBuilder("ansvarskod;förvaltning;team;nyckel;requests;tokens;kostnad USD\r\n");
        foreach (var r in rows)
        {
            csv.AppendJoin(CsvDelimiter, [Csv(r.CostCenterCode), Csv(r.DepartmentName), Csv(r.TeamName), Csv(r.KeyName),
                r.Requests.ToString(CultureInfo.InvariantCulture), r.Tokens.ToString(CultureInfo.InvariantCulture), Csv(r.Cost.ToString(CultureInfo.GetCultureInfo("sv-SE")))]).Append("\r\n");
        }
        await ctx.AuditAsync(AuditActions.Export, AuditEntities.Usage, "usage.csv", new { from = start, to = end, rows = rows.Count }, ct);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public async Task<PageDto<AuditEntryDto>> AuditAsync(int? page, int? pageSize, string? entityType, CancellationToken ct)
    {
        var (skip, take) = ReportQuery.Pagination(page, pageSize);
        var query = ctx.Db.AuditLog.AsNoTracking();
        if (!string.IsNullOrEmpty(entityType)) { query = query.Where(a => a.EntityType == entityType); }
        var items = await query.OrderByDescending(a => a.Id).Skip(skip).Take(take)
            .Select(a => new AuditEntryDto(a.Id, a.Timestamp, a.Actor, a.Action, a.EntityType, a.EntityId, a.Details,
                a.ActorName, a.ActorRoles, a.ActorDepartments, a.SourceAddress, a.SessionId, a.RequestId, a.UserAgent)).ToListAsync(ct);
        return new PageDto<AuditEntryDto>(items, await query.CountAsync(ct));
    }

    /// <summary>A cell of the semicolon-separated export (see <see cref="CsvCell"/>).</summary>
    internal static string Csv(string value) => CsvCell.Escape(value, CsvDelimiter);

    private static UsageRequestDto UsageDto(UsageRow row)
    {
        var u = row.Record;
        return new UsageRequestDto(u.RequestId, u.Timestamp, row.KeyPrefix, row.KeyName, row.TeamName, row.DepartmentName, GatewayEndpoints.Info(u.Endpoint).ClientPath,
            u.RequestedModel, u.ProviderName, u.UpstreamModel, u.InputTokens, u.CachedInputTokens, u.OutputTokens,
            u.LatencyMs, u.StatusCode, u.Outcome, u.FallbackCount, u.Streamed, u.PiiActionApplied, u.PiiCategories, u.ErrorCode, u.RoutingRuleId, u.RoutingRuleName,
            u.CostUsd, u.UpstreamMs, u.TimeToFirstTokenMs, u.GuardFlags, u.ProviderFirstTokenMs);
    }
}
