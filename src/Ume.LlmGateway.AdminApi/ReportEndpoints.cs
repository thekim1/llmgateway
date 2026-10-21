using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi;

public sealed record UsageRow
{
    public required UsageRecord Record { get; init; }
    public required string KeyPrefix { get; init; }
    public required string KeyName { get; init; }
    public required string TeamName { get; init; }
    public required string DepartmentName { get; init; }
    public required string CostCenterCode { get; init; }
}
public sealed record UsageSummaryRow(string Key, string Label, long Requests, long InputTokens, long OutputTokens, decimal CostSek, long Errors, long Fallbacks);

/// <summary>A usage record or a monthly total, in the shape the summary and the export add up (<see cref="ReportEndpoints.Facts"/>).</summary>
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
    public decimal CostSek { get; init; }
    public long Errors { get; init; }
    public long Fallbacks { get; init; }
}

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

public static class ReportEndpoints
{
    public static void MapReports(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("").RequireAuthorization(AdminPolicies.Read).WithTags("Reports");
        reports.MapGet("/usage/requests", async (DateTimeOffset? from, DateTimeOffset? to, Guid? keyId, Guid? teamId, Guid? departmentId, RequestOutcome? outcome, int? page, int? pageSize, AdminContext ctx, CancellationToken ct) =>
        {
            var (skip, take) = Pagination(page, pageSize);
            var (start, end) = Range(from, to, ctx.Now);
            var query = ctx.Usage().Where(u => u.Timestamp >= start && u.Timestamp < end);
            if (keyId is { } key) { query = query.Where(u => u.VirtualKeyId == key); }
            if (teamId is { } team) { query = query.Where(u => u.TeamId == team); }
            if (departmentId is { } department) { query = query.Where(u => u.DepartmentId == department); }
            if (outcome is { } result) { query = query.Where(u => u.Outcome == result); }
            var total = await query.CountAsync(ct);
            var rows = await WithNames(query.OrderByDescending(u => u.Timestamp).ThenByDescending(u => u.Id).Skip(skip).Take(take), ctx.Db).ToListAsync(ct);
            return TypedResults.Ok(new PageDto<UsageRequestDto>([.. rows.Select(UsageDto)], total));
        }).WithName("ListUsageRequests").WithSummary("Requests in the user's departments, newest first");
        reports.MapGet("/usage/requests/{requestId}", async (string requestId, AdminContext ctx, CancellationToken ct) =>
        {
            if (requestId.Length > 64) { throw new ApiFaultException(400, "The request id is too long."); }
            var row = await WithNames(ctx.Usage().Where(u => u.RequestId == requestId), ctx.Db).FirstOrDefaultAsync(ct) ?? throw new ApiFaultException(404, "The request does not exist.");
            return TypedResults.Ok(UsageDto(row));
        }).WithName("GetUsageRequest");
        reports.MapGet("/usage/summary", async (DateTimeOffset? from, DateTimeOffset? to, string? groupBy, Guid? departmentId, Guid? teamId, AdminContext ctx, CancellationToken ct) =>
        {
            var grouping = groupBy is null ? UsageGrouping.Department
                : EnumQuery.TryParse<UsageGrouping>(groupBy, out var parsed) ? parsed
                : throw new ApiFaultException(400, "The grouping is invalid.");
            var (start, end) = Range(from, to, ctx.Now);
            var retained = await Retained(ctx, start, end, ct);
            List<UsageSummaryRow> rows;
            if (grouping == UsageGrouping.Day)
            {
                // By day from records; a month that only has totals is one row labelled yyyy-MM.
                var query = ctx.Usage().Where(u => u.Timestamp >= retained.RecordsFrom && u.Timestamp < end);
                if (teamId is { } team) { query = query.Where(u => u.TeamId == team); }
                if (departmentId is { } department) { query = query.Where(u => u.DepartmentId == department); }
#pragma warning disable CA1305 // translated to SQL (a text cast in the database), no .NET culture is involved
                rows = await query.GroupBy(u => u.Timestamp.Date.ToString()).Select(g => new UsageSummaryRow(g.Key, g.Key,
#pragma warning restore CA1305
                    g.LongCount(), g.Sum(u => u.InputTokens), g.Sum(u => u.OutputTokens),
                    g.Sum(u => u.CostSek), g.LongCount(u => u.Outcome != RequestOutcome.Success), g.Sum(u => (long)u.FallbackCount))).ToListAsync(ct);
                if (retained.TotalsUntil is { } until)
                {
                    var totals = ctx.UsageTotals().Where(t => t.MonthStart >= retained.FirstMonth && t.MonthStart < until);
                    if (teamId is { } t1) { totals = totals.Where(t => t.TeamId == t1); }
                    if (departmentId is { } d1) { totals = totals.Where(t => t.DepartmentId == d1); }
                    var months = await totals.GroupBy(t => t.MonthStart).Select(g => new
                    {
                        g.Key, Requests = g.Sum(t => t.Requests), Input = g.Sum(t => t.InputTokens), Output = g.Sum(t => t.OutputTokens),
                        Cost = g.Sum(t => t.CostSek), Errors = g.Sum(t => t.Requests - t.SuccessfulRequests), Fallbacks = g.Sum(t => t.Fallbacks),
                    }).ToListAsync(ct);
                    rows.InsertRange(0, months.OrderBy(m => m.Key).Select(m =>
                    {
                        var label = TimeZoneInfo.ConvertTime(m.Key, BudgetPeriods.Stockholm).ToString("yyyy-MM", CultureInfo.InvariantCulture);
                        return new UsageSummaryRow(label, label, m.Requests, m.Input, m.Output, m.Cost, m.Errors, m.Fallbacks);
                    }));
                }
            }
            else
            {
                var facts = Facts(ctx, retained, end);
                if (teamId is { } team) { facts = facts.Where(f => f.TeamId == team); }
                if (departmentId is { } department) { facts = facts.Where(f => f.DepartmentId == department); }
                var names = FactsWithNames(facts, ctx.Db);
                var groups = grouping switch
                {
                    UsageGrouping.Department => names.GroupBy(u => new { Key = u.Fact.DepartmentId.ToString(), Label = u.DepartmentName }),
                    UsageGrouping.Team => names.GroupBy(u => new { Key = u.Fact.TeamId.ToString(), Label = u.TeamName }),
                    UsageGrouping.Key => names.GroupBy(u => new { Key = u.Fact.VirtualKeyId.ToString(), Label = u.KeyName }),
                    UsageGrouping.Model => names.GroupBy(u => new { Key = u.Fact.RequestedModel, Label = u.Fact.RequestedModel }),
                    UsageGrouping.Provider => names.GroupBy(u => new { Key = u.Fact.ProviderName ?? "-", Label = u.Fact.ProviderName ?? "-" }),
                    _ => throw new ArgumentOutOfRangeException(nameof(groupBy), grouping, "Unknown grouping."),
                };
                rows = await groups.Select(g => new UsageSummaryRow(g.Key.Key, g.Key.Label,
                    g.Sum(u => u.Fact.Requests), g.Sum(u => u.Fact.InputTokens), g.Sum(u => u.Fact.OutputTokens),
                    g.Sum(u => u.Fact.CostSek), g.Sum(u => u.Fact.Errors), g.Sum(u => u.Fact.Fallbacks))).ToListAsync(ct);
            }

            return TypedResults.Ok(new UsageSummaryDto(start, end, rows.Sum(r => r.CostSek), rows.Sum(r => r.Requests),
                rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens), rows.Sum(r => r.Errors), rows, retained.TotalsUntil is null ? null : retained.RecordsFrom));
        }).WithName("GetUsageSummary").WithSummary("Totals grouped by department, team, key, model, provider or day");
        reports.MapGet("/usage/metrics", async (DateTimeOffset? from, DateTimeOffset? to, string? bucket, Guid? departmentId, Guid? teamId, Guid? keyId, string? model, string? provider, RequestOutcome? outcome, GatewayEndpoint? endpoint, UsageMetricsService metrics, CancellationToken ct) =>
            TypedResults.Ok(await metrics.GetAsync(new UsageMetricsQuery(from, to, bucket, departmentId, teamId, keyId, model, provider, outcome, endpoint), ct)))
            .WithName("GetUsageMetrics").WithSummary("Dashboard figures per hour or day: requests by outcome, cost in USD, tokens, cache hits, latency, gateway overhead, time to first token and throughput");
        reports.MapGet("/usage/pii-incidents", async (DateTimeOffset? from, DateTimeOffset? to, Guid? keyId, Guid? teamId, Guid? departmentId, string? category, string? action, int? page, int? pageSize, PiiIncidentService pii, CancellationToken ct) =>
            TypedResults.Ok(await pii.ListAsync(new PiiIncidentQuery(from, to, keyId, teamId, departmentId, category, action, page, pageSize), ct)))
            .WithName("ListPiiIncidents").WithSummary("Requests in which PII was detected, newest first, with counts per category and action");
        reports.MapGet("/usage/export.csv", async (DateTimeOffset? from, DateTimeOffset? to, AdminContext ctx, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to, ctx.Now);
            var query = FactsWithNames(Facts(ctx, await Retained(ctx, start, end, ct), end), ctx.Db);
            var rows = await query.GroupBy(u => new { u.CostCenterCode, u.DepartmentName, u.TeamName, u.KeyName })
                .Select(g => new { g.Key.CostCenterCode, g.Key.DepartmentName, g.Key.TeamName, g.Key.KeyName,
                    Requests = g.Sum(u => u.Fact.Requests), Tokens = g.Sum(u => u.Fact.InputTokens + u.Fact.OutputTokens), Cost = g.Sum(u => u.Fact.CostSek) }).ToListAsync(ct);
            var csv = new StringBuilder("ansvarskod;förvaltning;team;nyckel;requests;tokens;kostnad SEK\r\n");
            foreach (var r in rows)
            {
                csv.AppendJoin(CsvDelimiter, [Csv(r.CostCenterCode), Csv(r.DepartmentName), Csv(r.TeamName), Csv(r.KeyName),
                    r.Requests.ToString(CultureInfo.InvariantCulture), r.Tokens.ToString(CultureInfo.InvariantCulture), Csv(r.Cost.ToString(CultureInfo.GetCultureInfo("sv-SE")))]).Append("\r\n");
            }
            await ctx.AuditAsync(AuditActions.Export, AuditEntities.Usage, "usage.csv", new { from = start, to = end, rows = rows.Count }, ct);
            return TypedResults.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "anvandning.csv");
        }).WithName("ExportUsageCsv").WithSummary("Cost per cost centre, department, team and key (semicolon-separated, Swedish decimals)");
        api.MapGet("/audit", async (int? page, int? pageSize, string? entityType, AdminContext ctx, CancellationToken ct) =>
        {
            var (skip, take) = Pagination(page, pageSize);
            var query = ctx.Db.AuditLog.AsNoTracking();
            if (!string.IsNullOrEmpty(entityType)) { query = query.Where(a => a.EntityType == entityType); }
            var items = await query.OrderByDescending(a => a.Id).Skip(skip).Take(take)
                .Select(a => new AuditEntryDto(a.Id, a.Timestamp, a.Actor, a.Action, a.EntityType, a.EntityId, a.Details,
                    a.ActorName, a.ActorRoles, a.ActorDepartments, a.SourceAddress, a.SessionId, a.RequestId, a.UserAgent)).ToListAsync(ct);
            return TypedResults.Ok(new PageDto<AuditEntryDto>(items, await query.CountAsync(ct)));
        }).RequireAuthorization(AdminPolicies.Admin).WithTags("Reports").WithName("ListAudit");
        api.MapGet("/catalog", async (AdminContext ctx, ProviderCatalogService catalog, IOptions<GatewayLinkOptions> gateway, CancellationToken ct) =>
        {
            var now = ctx.Now;
            var rate = (await ctx.Db.CurrentRateAsync(now, ct))?.SekPerUnit ?? throw new ApiFaultException(503, "No exchange rate is set.");
            var models = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices)
                .Where(m => m.IsEnabled && m.ProviderAccount!.IsEnabled && !m.ProviderAccount.IsDrained).ToListAsync(ct);
            var byId = models.ToDictionary(m => m.Id);
            // Public names over every model, as the gateway decides them (a disabled model still shares its name).
            var names = PublicModelNames.Public(await ctx.Db.ModelDeployments.AsNoTracking().Include(m => m.ProviderAccount).ToListAsync(ct));
            var routes = await catalog.Routes().Where(r => r.IsEnabled).ToListAsync(ct);
            return TypedResults.Ok(new CatalogDto(
                gateway.Value.PublicBaseUrl,
                [.. models.Select(m => new CatalogModelDto(names[m.Id], m.Kind, m.ProviderAccount!.Residency, ProviderCatalogService.Capabilities(m.ProviderAccount.Capabilities),
                    m.PriceAt(now)?.InputPerMillionUsd * rate, m.PriceAt(now)?.OutputPerMillionUsd * rate))],
                [.. routes.Where(r => r.Targets.Any(t => byId.ContainsKey(t.ModelDeploymentId))).Select(r =>
                {
                    var targets = r.Targets.Where(t => byId.ContainsKey(t.ModelDeploymentId)).Select(t => byId[t.ModelDeploymentId]).ToArray();
                    return new CatalogRouteDto(r.Name, r.Description, r.Kind, [.. targets.Select(m => m.ProviderAccount!.Residency).Distinct()],
                        ProviderCatalogService.Capabilities(targets.Aggregate(ProviderCapabilities.None, (a, m) => a | m.ProviderAccount!.Capabilities)),
                        targets.Max(m => m.PriceAt(now)?.InputPerMillionUsd * rate), targets.Max(m => m.PriceAt(now)?.OutputPerMillionUsd * rate));
                })]));
        }).WithTags("Reports").WithName("GetCatalog").WithSummary("Enabled models and routes with prices in SEK, for key holders");
    }

    private const char CsvDelimiter = ';';

    internal static IQueryable<UsageRow> WithNames(IQueryable<UsageRecord> query, GatewayDbContext db) =>
        from u in query
        join key in db.VirtualKeys on u.VirtualKeyId equals key.Id
        join team in db.Teams on u.TeamId equals team.Id
        join department in db.Departments on u.DepartmentId equals department.Id
        select new UsageRow { Record = u, KeyPrefix = key.Prefix, KeyName = key.Name, TeamName = team.Name, DepartmentName = department.Name, CostCenterCode = department.CostCenterCode };

    /// <summary>
    /// Where a report period's usage comes from after data retention: records from <c>RecordsFrom</c>, and, when the period
    /// reaches back before the oldest kept record, the monthly totals of months from <c>FirstMonth</c> (the month the period
    /// starts in) up to <c>TotalsUntil</c>.
    /// </summary>
    internal readonly record struct RetainedRange(DateTimeOffset RecordsFrom, DateTimeOffset FirstMonth, DateTimeOffset? TotalsUntil);

    internal static async Task<RetainedRange> Retained(AdminContext ctx, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var kept = await ctx.Db.UsageRetainedFromAsync(ct);
        if (kept is not { } from || from <= start)
        {
            return new RetainedRange(start, start, null);
        }

        return new RetainedRange(from, BudgetPeriods.GetWindow(BudgetPeriod.Monthly, start).Start, from < end ? from : end);
    }

    /// <summary>Records and, before the oldest kept record, monthly totals, in the user's departments (see <see cref="RetainedRange"/>).</summary>
    internal static IQueryable<UsageFact> Facts(AdminContext ctx, RetainedRange range, DateTimeOffset end)
    {
        var records = ctx.Usage().AsNoTracking().Where(u => u.Timestamp >= range.RecordsFrom && u.Timestamp < end).Select(u => new UsageFact
        {
            DepartmentId = u.DepartmentId, TeamId = u.TeamId, VirtualKeyId = u.VirtualKeyId, RequestedModel = u.RequestedModel, ProviderName = u.ProviderName,
            Requests = 1, InputTokens = u.InputTokens, OutputTokens = u.OutputTokens, CostSek = u.CostSek,
            Errors = u.Outcome != RequestOutcome.Success ? 1 : 0, Fallbacks = u.FallbackCount,
        });
        if (range.TotalsUntil is not { } until)
        {
            return records;
        }

        return records.Concat(ctx.UsageTotals().Where(t => t.MonthStart >= range.FirstMonth && t.MonthStart < until).Select(t => new UsageFact
        {
            DepartmentId = t.DepartmentId, TeamId = t.TeamId, VirtualKeyId = t.VirtualKeyId, RequestedModel = t.RequestedModel, ProviderName = t.ProviderName,
            Requests = t.Requests, InputTokens = t.InputTokens, OutputTokens = t.OutputTokens, CostSek = t.CostSek,
            Errors = t.Requests - t.SuccessfulRequests, Fallbacks = t.Fallbacks,
        }));
    }

    // Member initialisers, not a constructor: EF translates GroupBy over the properties only then.
    internal sealed class UsageFactRow
    {
        public required UsageFact Fact { get; init; }
        public required string KeyName { get; init; }
        public required string TeamName { get; init; }
        public required string DepartmentName { get; init; }
        public required string CostCenterCode { get; init; }
    }

    internal static IQueryable<UsageFactRow> FactsWithNames(IQueryable<UsageFact> facts, GatewayDbContext db) =>
        from f in facts
        join key in db.VirtualKeys on f.VirtualKeyId equals key.Id
        join team in db.Teams on f.TeamId equals team.Id
        join department in db.Departments on f.DepartmentId equals department.Id
        select new UsageFactRow { Fact = f, KeyName = key.Name, TeamName = team.Name, DepartmentName = department.Name, CostCenterCode = department.CostCenterCode };

    private static UsageRequestDto UsageDto(UsageRow row)
    {
        var u = row.Record;
        return new UsageRequestDto(u.RequestId, u.Timestamp, row.KeyPrefix, row.KeyName, row.TeamName, row.DepartmentName, GatewayEndpoints.Info(u.Endpoint).ClientPath,
            u.RequestedModel, u.ProviderName, u.UpstreamModel, u.InputTokens, u.CachedInputTokens, u.OutputTokens, u.CostSek,
            u.LatencyMs, u.StatusCode, u.Outcome, u.FallbackCount, u.Streamed, u.PiiActionApplied, u.PiiCategories, u.ErrorCode, u.RoutingRuleId, u.RoutingRuleName,
            u.CostUsd, u.UpstreamMs, u.TimeToFirstTokenMs);
    }

    internal static (int Skip, int Take) Pagination(int? page, int? pageSize)
    {
        var number = page ?? 1; var size = pageSize ?? 50;
        if (number is < 1 or > 100000 || size is < 1 or > 200) { throw new ApiFaultException(400, "The page number or page size is invalid."); }
        return ((number - 1) * size, size);
    }
    internal static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        var start = from?.ToUniversalTime() ?? now.AddDays(-30);
        var end = to?.ToUniversalTime() ?? now;
        if (start >= end || end - start > TimeSpan.FromDays(366)) { throw new ApiFaultException(400, "Choose a time range of at most one year."); }
        return (start, end);
    }

    /// <summary>A cell of the semicolon-separated export (see <see cref="CsvCell"/>).</summary>
    internal static string Csv(string value) => CsvCell.Escape(value, CsvDelimiter);
}
