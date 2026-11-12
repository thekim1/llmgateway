using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Budgets;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Budgets;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Integrations;

/// <summary>
/// The Integration API (docs/integrations.md): usage and budget status of one integration's keys, for the platform
/// itself. Only the calling credential key's integration is ever read; keys and budgets are not changed here.
/// </summary>
public sealed class IntegrationApiService(AdminContext ctx, BudgetAdminService budgets)
{
    public const int MaxDays = 366;
    public static readonly IReadOnlyList<string> Dimensions = ["key", "model", "step"];

    /// <summary>
    /// Requests, tokens and cost of the integration's keys (credential and assigned, revoked ones included) per Stockholm
    /// day or month, optionally per key, model and step. Like the Data API, months before the oldest kept usage record come
    /// from the monthly totals (which have no step), and days cover kept records only. <paramref name="runId"/> limits the
    /// answer to one run, from the records only.
    /// </summary>
    public async Task<IntegrationUsageDto> UsageAsync(Guid integrationId, DateOnly from, DateOnly to, string? granularity, string? groupBy,
        Guid? keyId, string? runId, CancellationToken ct)
    {
        var grouping = granularity is null ? UsageGrouping.Day
            : EnumQuery.TryParse<UsageGrouping>(granularity, out var parsed) ? parsed
            : throw new ApiFaultException(400, "'granularity' must be day or month.");
        var dimensions = (groupBy ?? "key").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.ToLowerInvariant()).Distinct().ToList();
        if (dimensions.FirstOrDefault(d => !Dimensions.Contains(d)) is { } unknown)
        {
            throw new ApiFaultException(400, $"'groupBy' takes {string.Join(", ", Dimensions)}; '{unknown}' is not one of them.");
        }

        if (grouping == UsageGrouping.Month)
        {
            from = new DateOnly(from.Year, from.Month, 1);
            to = new DateOnly(to.Year, to.Month, 1).AddMonths(1).AddDays(-1);
        }

        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxDays)
        {
            throw new ApiFaultException(400, $"'to' must be on or after 'from', and the range at most {MaxDays} days.");
        }

        var runFilter = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();
        var keys = await ctx.Db.VirtualKeys.AsNoTracking().Where(k => k.IntegrationId == integrationId)
            .Select(k => new { k.Id, k.Name }).ToDictionaryAsync(k => k.Id, k => k.Name, ct);
        if (keyId is { } wanted && !keys.ContainsKey(wanted))
        {
            throw NotFound.Key();
        }

        Guid[] ids = keyId is { } only ? [only] : [.. keys.Keys];
        var zone = BudgetPeriods.Stockholm;
        var start = LocalMidnightUtc(from, zone);
        var end = LocalMidnightUtc(to.AddDays(1), zone);
        var retainedFrom = await ctx.Db.UsageRetainedFromAsync(ct);
        var recordsFrom = retainedFrom is { } kept && kept > start ? kept : start;
        // Monthly totals only for a month granularity and without a run filter (they keep neither days nor runs).
        var totalsUntil = grouping == UsageGrouping.Month && runFilter is null && retainedFrom is { } k && k > start ? (k < end ? k : end) : start;
        var unit = EnumQuery.Name(grouping);

        var rows = ids.Length == 0 ? [] : await ctx.Db.Database.SqlQuery<UsageSqlRow>($"""
            SELECT u."Period", u."VirtualKeyId", u."RequestedModel", u."Step",
                   sum(u."Requests")::bigint AS "Requests", sum(u."SuccessfulRequests")::bigint AS "SuccessfulRequests",
                   sum(u."InputTokens")::bigint AS "InputTokens", sum(u."CachedInputTokens")::bigint AS "CachedInputTokens",
                   sum(u."OutputTokens")::bigint AS "OutputTokens", sum(u."CostUsd") AS "CostUsd"
            FROM (
                SELECT date_trunc({unit}, "Timestamp" AT TIME ZONE 'Europe/Stockholm')::date AS "Period", "VirtualKeyId", "RequestedModel",
                       "Step", 1::bigint AS "Requests", CASE WHEN "Outcome" = 'Success' THEN 1 ELSE 0 END::bigint AS "SuccessfulRequests",
                       "InputTokens", "CachedInputTokens", "OutputTokens", "CostUsd"
                FROM "UsageRecords"
                WHERE "VirtualKeyId" = ANY({ids}) AND "Timestamp" >= {recordsFrom} AND "Timestamp" < {end}
                  AND ({runFilter}::text IS NULL OR "RunId" = {runFilter})
                UNION ALL
                SELECT ("MonthStart" AT TIME ZONE 'Europe/Stockholm')::date, "VirtualKeyId", "RequestedModel", NULL, "Requests",
                       "SuccessfulRequests", "InputTokens", "CachedInputTokens", "OutputTokens", "CostUsd"
                FROM "UsageMonthlyTotals"
                WHERE "VirtualKeyId" = ANY({ids}) AND "MonthStart" >= {start} AND "MonthStart" < {totalsUntil}
            ) u
            GROUP BY u."Period", u."VirtualKeyId", u."RequestedModel", u."Step"
            """).ToListAsync(ct);

        var byKey = dimensions.Contains("key");
        var byModel = dimensions.Contains("model");
        var byStep = dimensions.Contains("step");
        var items = rows
            .GroupBy(r => (r.Period, Key: byKey ? r.VirtualKeyId : (Guid?)null, Model: byModel ? r.RequestedModel : null, Step: byStep ? r.Step : null))
            .Select(g => new IntegrationUsageRow(g.Key.Period, g.Key.Key, g.Key.Key is { } id ? keys.GetValueOrDefault(id) : null, g.Key.Model, g.Key.Step,
                g.Sum(r => r.Requests), g.Sum(r => r.SuccessfulRequests), g.Sum(r => r.InputTokens), g.Sum(r => r.CachedInputTokens),
                g.Sum(r => r.OutputTokens), g.Sum(r => r.CostUsd)))
            .OrderBy(r => r.Period).ThenBy(r => r.KeyName, StringComparer.Ordinal).ThenBy(r => r.Model, StringComparer.Ordinal)
            .ThenBy(r => r.Step, StringComparer.Ordinal)
            .ToList();
        return new IntegrationUsageDto(from, to, unit, dimensions, items);
    }

    /// <summary>
    /// The active budgets that apply to <paramref name="keyId"/>, one of the integration's keys, as the gateway applies them:
    /// the key's own and its rotation predecessors', its team's and its department's, with what each used this period.
    /// </summary>
    public async Task<IntegrationBudgetsDto> BudgetsAsync(Guid integrationId, Guid keyId, CancellationToken ct)
    {
        var key = await ctx.Db.VirtualKeys.AsNoTracking().Include(k => k.Team)
            .SingleOrDefaultAsync(k => k.Id == keyId && k.IntegrationId == integrationId, ct) ?? throw NotFound.Key();
        var lineage = KeyRotation.Ancestors(key.Id, await ctx.Db.KeyRotationMapAsync(ct)).ToArray();
        var departmentId = key.Team?.DepartmentId ?? Guid.Empty;
        var applicable = await ctx.Db.Budgets.AsNoTracking().Where(b => b.IsActive && (
                (b.Scope == BudgetScope.VirtualKey && lineage.Contains(b.ScopeId)) ||
                (b.Scope == BudgetScope.Team && b.ScopeId == key.TeamId) ||
                (b.Scope == BudgetScope.Department && b.ScopeId == departmentId)))
            .OrderBy(b => b.Scope).ThenBy(b => b.Period).ToListAsync(ct);
        var spent = await budgets.SpentAsync(applicable, ct);
        var items = applicable.Select(b =>
        {
            var window = BudgetPeriods.GetWindow(b.Period, ctx.Now);
            var used = spent.GetValueOrDefault(b.Id);
            var percent = b.Limit > 0 ? decimal.Round(used / b.Limit * 100, 1) : used > 0 ? 100 : 0;
            return new IntegrationBudgetDto(b.Id, b.Scope, b.ScopeId, b.Unit, b.Limit, b.Residency, b.Period, window.Start, window.End, used, percent,
                used >= b.Limit);
        }).ToList();
        return new IntegrationBudgetsDto(key.Id, items);
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Raw result of the usage query.</summary>
    public sealed class UsageSqlRow
    {
        public DateOnly Period { get; set; }
        public Guid VirtualKeyId { get; set; }
        public string RequestedModel { get; set; } = "";
        public string? Step { get; set; }
        public long Requests { get; set; }
        public long SuccessfulRequests { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public decimal CostUsd { get; set; }
    }
}
