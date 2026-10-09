using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
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

public static class ReportEndpoints
{
    public static void MapReports(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("").RequireAuthorization("read");
        reports.MapGet("/usage/requests", async (DateTimeOffset? from, DateTimeOffset? to, Guid? keyId, Guid? teamId, Guid? departmentId, RequestOutcome? outcome, int? page, int? pageSize, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var (skip, take) = Pagination(page, pageSize);
            var (start, end) = Range(from, to, ctx.Now);
            var query = ctx.Usage(user).Where(u => u.Timestamp >= start && u.Timestamp < end);
            if (keyId is { } key) { query = query.Where(u => u.VirtualKeyId == key); }
            if (teamId is { } team) { query = query.Where(u => u.TeamId == team); }
            if (departmentId is { } department) { query = query.Where(u => u.DepartmentId == department); }
            if (outcome is { } result) { query = query.Where(u => u.Outcome == result); }
            var total = await query.CountAsync(ct);
            var rows = await WithNames(query.OrderByDescending(u => u.Timestamp).ThenByDescending(u => u.Id).Skip(skip).Take(take), ctx.Db).ToListAsync(ct);
            return Results.Ok(new { items = rows.Select(UsageDto), total });
        });
        reports.MapGet("/usage/requests/{requestId}", async (string requestId, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (requestId.Length > 64) { throw new AdminFaultException(400, "Anrops-ID är för långt."); }
            var row = await WithNames(ctx.Usage(user).Where(u => u.RequestId == requestId), ctx.Db).FirstOrDefaultAsync(ct) ?? throw new AdminFaultException(404, "Anropet finns inte.");
            return Results.Ok(UsageDto(row));
        });
        reports.MapGet("/usage/summary", async (DateTimeOffset? from, DateTimeOffset? to, string? groupBy, Guid? departmentId, Guid? teamId, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to, ctx.Now);
            var query = ctx.Usage(user).Where(u => u.Timestamp >= start && u.Timestamp < end);
            if (teamId is { } team) { query = query.Where(u => u.TeamId == team); }
            if (departmentId is { } department) { query = query.Where(u => u.DepartmentId == department); }
            var names = WithNames(query, ctx.Db);
            var grouping = (groupBy ?? "department") switch
            {
                "department" => names.GroupBy(u => new { Key = u.Record.DepartmentId.ToString(), Label = u.DepartmentName }),
                "team" => names.GroupBy(u => new { Key = u.Record.TeamId.ToString(), Label = u.TeamName }),
                "key" => names.GroupBy(u => new { Key = u.Record.VirtualKeyId.ToString(), Label = u.KeyName }),
                "model" => names.GroupBy(u => new { Key = u.Record.RequestedModel, Label = u.Record.RequestedModel }),
                "provider" => names.GroupBy(u => new { Key = u.Record.ProviderName ?? "-", Label = u.Record.ProviderName ?? "-" }),
                "day" => names.GroupBy(u => new { Key = u.Record.Timestamp.Date.ToString(), Label = u.Record.Timestamp.Date.ToString() }),
                _ => throw new AdminFaultException(400, "Grupperingen är ogiltig."),
            };
            var rows = await grouping.Select(g => new UsageSummaryRow(g.Key.Key, g.Key.Label,
                g.LongCount(), g.Sum(u => u.Record.InputTokens), g.Sum(u => u.Record.OutputTokens),
                g.Sum(u => u.Record.CostSek), g.LongCount(u => u.Record.Outcome != RequestOutcome.Success), g.Sum(u => (long)u.Record.FallbackCount))).ToListAsync(ct);
            return Results.Ok(new { from = start, to = end, totalCostSek = rows.Sum(r => r.CostSek), totalRequests = rows.Sum(r => r.Requests),
                totalInputTokens = rows.Sum(r => r.InputTokens), totalOutputTokens = rows.Sum(r => r.OutputTokens), totalErrors = rows.Sum(r => r.Errors), rows });
        });
        reports.MapGet("/usage/export.csv", async (DateTimeOffset? from, DateTimeOffset? to, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to, ctx.Now);
            var query = WithNames(ctx.Usage(user).Where(u => u.Timestamp >= start && u.Timestamp < end), ctx.Db);
            var rows = await query.GroupBy(u => new { u.CostCenterCode, u.DepartmentName, u.TeamName, u.KeyName })
                .Select(g => new { g.Key.CostCenterCode, g.Key.DepartmentName, g.Key.TeamName, g.Key.KeyName,
                    Requests = g.LongCount(), Tokens = g.Sum(u => u.Record.InputTokens + u.Record.OutputTokens), Cost = g.Sum(u => u.Record.CostSek) }).ToListAsync(ct);
            var csv = new StringBuilder("ansvarskod;förvaltning;team;nyckel;requests;tokens;kostnad SEK\r\n");
            foreach (var r in rows)
            {
                csv.AppendJoin(';', [Csv(r.CostCenterCode), Csv(r.DepartmentName), Csv(r.TeamName), Csv(r.KeyName), r.Requests.ToString(CultureInfo.InvariantCulture), r.Tokens.ToString(CultureInfo.InvariantCulture), r.Cost.ToString(CultureInfo.GetCultureInfo("sv-SE"))]).Append("\r\n");
            }
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "anvandning.csv");
        });
        api.MapGet("/audit", async (int? page, int? pageSize, string? entityType, AdminContext ctx, CancellationToken ct) =>
        {
            var (skip, take) = Pagination(page, pageSize);
            var query = ctx.Db.AuditLog.AsNoTracking();
            if (!string.IsNullOrEmpty(entityType)) { query = query.Where(a => a.EntityType == entityType); }
            return Results.Ok(new { items = await query.OrderByDescending(a => a.Id).Skip(skip).Take(take).Select(a => new { a.Id, a.Timestamp, a.Actor, a.Action, a.EntityType, a.EntityId, a.Details }).ToListAsync(ct), total = await query.CountAsync(ct) });
        }).RequireAuthorization("admin");
        api.MapGet("/catalog", async (AdminContext ctx, IConfiguration configuration, CancellationToken ct) =>
        {
            var now = ctx.Now;
            var rate = await ctx.Db.ExchangeRates.Where(r => r.Currency == "USD" && r.EffectiveFrom <= now).OrderByDescending(r => r.EffectiveFrom).Select(r => (decimal?)r.SekPerUnit).FirstOrDefaultAsync(ct)
                ?? throw new AdminFaultException(503, "Växelkurs saknas.");
            var models = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices)
                .Where(m => m.IsEnabled && m.ProviderAccount!.IsEnabled && !m.ProviderAccount.IsDrained).ToListAsync(ct);
            var modelIds = models.Select(m => m.Id).ToHashSet();
            var routes = await ConfigurationEndpoints.Routes(ctx).Where(r => r.IsEnabled).ToListAsync(ct);
            return Results.Ok(new
            {
                gatewayBaseUrl = configuration["Gateway:BaseUrl"] ?? "https://localhost:5140",
                models = models.Select(m => new { m.Name, m.Kind, residency = m.ProviderAccount!.Residency, capabilities = ConfigurationEndpoints.Capabilities(m.ProviderAccount.Capabilities),
                    inputSekPerMillion = m.PriceAt(now)?.InputPerMillionUsd * rate, outputSekPerMillion = m.PriceAt(now)?.OutputPerMillionUsd * rate }),
                routes = routes.Where(r => r.Targets.Any(t => modelIds.Contains(t.ModelDeploymentId))).Select(r =>
                {
                    var targets = r.Targets.Where(t => modelIds.Contains(t.ModelDeploymentId)).Select(t => models.Single(m => m.Id == t.ModelDeploymentId)).ToArray();
                    return new { r.Name, r.Description, r.Kind, residencies = targets.Select(m => m.ProviderAccount!.Residency).Distinct().ToArray(),
                        capabilities = ConfigurationEndpoints.Capabilities(targets.Aggregate(ProviderCapabilities.None, (a, m) => a | m.ProviderAccount!.Capabilities)),
                        inputSekPerMillion = targets.Max(m => m.PriceAt(now)?.InputPerMillionUsd * rate), outputSekPerMillion = targets.Max(m => m.PriceAt(now)?.OutputPerMillionUsd * rate) };
                }),
            });
        });
    }

    private static IQueryable<UsageRow> WithNames(IQueryable<UsageRecord> query, GatewayDbContext db) =>
        from u in query
        join key in db.VirtualKeys on u.VirtualKeyId equals key.Id
        join team in db.Teams on u.TeamId equals team.Id
        join department in db.Departments on u.DepartmentId equals department.Id
        select new UsageRow { Record = u, KeyPrefix = key.Prefix, KeyName = key.Name, TeamName = team.Name, DepartmentName = department.Name, CostCenterCode = department.CostCenterCode };

    private static object UsageDto(UsageRow row)
    {
        var u = row.Record;
        return new { u.RequestId, u.Timestamp, row.KeyPrefix, row.KeyName, row.TeamName, row.DepartmentName,
            endpoint = u.Endpoint switch { GatewayEndpoint.ChatCompletions => "/v1/chat/completions", GatewayEndpoint.Embeddings => "/v1/embeddings", GatewayEndpoint.Responses => "/v1/responses", _ => "/v1/messages" },
            u.RequestedModel, u.ProviderName, u.UpstreamModel, u.InputTokens, u.CachedInputTokens, u.OutputTokens, u.CostSek,
            u.LatencyMs, u.StatusCode, u.Outcome, u.FallbackCount, u.Streamed, u.PiiActionApplied, u.PiiCategories, u.ErrorCode, u.RoutingRuleId, u.RoutingRuleName };
    }
    internal static (int Skip, int Take) Pagination(int? page, int? pageSize)
    {
        var number = page ?? 1; var size = pageSize ?? 50;
        if (number is < 1 or > 100000 || size is < 1 or > 200) { throw new AdminFaultException(400, "Sidnummer eller sidstorlek är ogiltig."); }
        return ((number - 1) * size, size);
    }
    private static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        var start = from?.ToUniversalTime() ?? now.AddDays(-30);
        var end = to?.ToUniversalTime() ?? now;
        if (start >= end || end - start > TimeSpan.FromDays(366)) { throw new AdminFaultException(400, "Välj ett tidsintervall på högst ett år."); }
        return (start, end);
    }
    internal static string Csv(string value)
    {
        if (value.AsSpan().TrimStart().StartsWith("=") || value.AsSpan().TrimStart().StartsWith("+") ||
            value.AsSpan().TrimStart().StartsWith("-") || value.AsSpan().TrimStart().StartsWith("@") ||
            value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n'))
        {
            value = "'" + value;
        }
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
