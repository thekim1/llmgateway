using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.DataApi;

public sealed record UsageRecordItem(
    long Id, string RequestId, DateTimeOffset Timestamp, Guid KeyId, Guid TeamId, Guid DepartmentId, GatewayEndpoint Endpoint,
    string RequestedModel, Guid? ProviderId, string? ProviderName, Guid? ModelId, string? UpstreamModel,
    long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, decimal CostSek, int LatencyMs, int StatusCode,
    RequestOutcome Outcome, int FallbackCount, bool Streamed, PiiPolicy? PiiAction, string? PiiCategories, string? ErrorCode,
    Guid? RoutingRuleId, string? RoutingRuleName, DateTimeOffset RecordedAt, decimal AudioSeconds) : IFeedItem;

public sealed record SecurityRequestItem(
    long Id, string RequestId, DateTimeOffset Timestamp, Guid KeyId, Guid TeamId, Guid DepartmentId, GatewayEndpoint Endpoint,
    RequestOutcome Outcome, int StatusCode, string? ErrorCode, PiiPolicy? PiiAction, string? PiiCategories, DateTimeOffset RecordedAt) : IFeedItem;

public sealed record AuthFailureItem(
    long Id, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, AuthFailureReason Reason, GatewayEndpoint Endpoint, Guid? KeyId,
    string? KeyPrefix, Guid? TeamId, Guid? DepartmentId, string? SourceAddress, int Count, DateTimeOffset RecordedAt) : IFeedItem;

public sealed record AuditItem(
    long Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityType, string? EntityId, string? Details, DateTimeOffset RecordedAt) : IFeedItem;

public sealed record KeyInventoryItem(
    Guid Id, string Prefix, string Name, Guid TeamId, Guid? DepartmentId, KeyStatus Status, DateTimeOffset CreatedAt, string? CreatedBy,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? GraceUntil, Guid? RotatedToKeyId, DateTimeOffset? LastUsedAt,
    PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, IReadOnlyList<string> AllowedModels, IReadOnlyList<string> AllowedProviders,
    IReadOnlyList<DataResidency> AllowedResidencies, int? RequestsPerMinute, int? TokensPerMinute);

public sealed record AggregateItem(
    DateOnly Period, Guid? DepartmentId, string? CostCenterCode, string? DepartmentName, Guid? ModelId, string? ModelName, Guid? ProviderId,
    string? ProviderName, DataResidency? Residency, string Endpoint, long Requests, long SuccessfulRequests, long InputTokens,
    long CachedInputTokens, long OutputTokens, decimal CostSek, decimal CostUsd, decimal AudioSeconds);

/// <summary>Raw result of the aggregate query; names are joined in afterwards.</summary>
public sealed class AggregateRow
{
    public DateOnly Period { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ModelId { get; set; }
    public Guid? ProviderId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public long Requests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostSek { get; set; }
    public decimal CostUsd { get; set; }
    public decimal AudioSeconds { get; set; }
}

public static class DataEndpoints
{
    /// <summary>Error codes of policy refusals that the security feed includes (see gateway.request.refused).</summary>
    private static readonly string[] RefusalCodes = ["attachment_not_allowed", "model_not_allowed"];

    public static void MapDataEndpoints(this RouteGroupBuilder v1)
    {
        var usage = v1.MapGroup("/usage").WithTags("Usage");
        usage.MapGet("/records", async (string? after, int? limit, HttpContext http, GatewayDbContext db, IOptions<DataApiOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var settings = options.Value;
            var (cursor, size) = Feeds.Window(after, limit, settings);
            var pii = settings.Detail.IncludePiiCategories;
            var rows = await db.UsageRecords.AsNoTracking().Where(u => u.Id > cursor).OrderBy(u => u.Id).Take(size + 1)
                .Select(u => new UsageRecordItem(u.Id, u.RequestId, u.Timestamp, u.VirtualKeyId, u.TeamId, u.DepartmentId, u.Endpoint,
                    u.RequestedModel, u.ProviderAccountId, u.ProviderName, u.ModelDeploymentId, u.UpstreamModel, u.InputTokens,
                    u.CachedInputTokens, u.OutputTokens, u.CostUsd, u.CostSek, u.LatencyMs, u.StatusCode, u.Outcome, u.FallbackCount,
                    u.Streamed, u.PiiActionApplied, pii ? u.PiiCategories : null, u.ErrorCode, u.RoutingRuleId, u.RoutingRuleName, u.RecordedAt, u.AudioSeconds))
                .ToListAsync(ct);
            return DataResults.Feed(http, Feeds.Page(rows, cursor, size, Feeds.Cutoff(time, settings)));
        }).RequireAuthorization(DataPermissions.UsageDetail)
            .WithSummary("Usage records, one per request (incremental feed)");

        usage.MapGet("/aggregate", async (DateOnly from, DateOnly to, string? granularity, HttpContext http, GatewayDbContext db, IOptions<DataApiOptions> options, CancellationToken ct) =>
            DataResults.List(http, await AggregateAsync(db, options.Value, from, to, granularity, ct)))
            .RequireAuthorization(DataPermissions.UsageAggregate)
            .WithSummary("Totals per day or month, department, model, provider and endpoint (from and to are inclusive dates)");

        var security = v1.MapGroup("/security").WithTags("Security").RequireAuthorization(DataPermissions.SecurityRead);
        security.MapGet("/auth-failures", async (string? after, int? limit, HttpContext http, GatewayDbContext db, IOptions<DataApiOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var (cursor, size) = Feeds.Window(after, limit, options.Value);
            var rows = await db.AuthFailures.AsNoTracking().Where(f => f.Id > cursor).OrderBy(f => f.Id).Take(size + 1)
                .Select(f => new AuthFailureItem(f.Id, f.FirstSeen, f.LastSeen, f.Reason, f.Endpoint, f.VirtualKeyId, f.KeyPrefix, f.TeamId,
                    f.DepartmentId, f.SourceAddress, f.Count, f.RecordedAt))
                .ToListAsync(ct);
            return DataResults.Feed(http, Feeds.Page(rows, cursor, size, Feeds.Cutoff(time, options.Value)));
        }).WithSummary("Refused keys, counted per reason, endpoint, key and client network (incremental feed)");

        security.MapGet("/requests", async (string? after, int? limit, HttpContext http, GatewayDbContext db, IOptions<DataApiOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var (cursor, size) = Feeds.Window(after, limit, options.Value);
            var rows = await db.UsageRecords.AsNoTracking()
                .Where(u => u.Id > cursor && (u.PiiActionApplied != null || (u.ErrorCode != null && RefusalCodes.Contains(u.ErrorCode))))
                .OrderBy(u => u.Id).Take(size + 1)
                .Select(u => new SecurityRequestItem(u.Id, u.RequestId, u.Timestamp, u.VirtualKeyId, u.TeamId, u.DepartmentId, u.Endpoint,
                    u.Outcome, u.StatusCode, u.ErrorCode, u.PiiActionApplied, u.PiiCategories, u.RecordedAt))
                .ToListAsync(ct);
            return DataResults.Feed(http, Feeds.Page(rows, cursor, size, Feeds.Cutoff(time, options.Value)));
        }).WithSummary("Requests where personal data was found or a policy refused them (incremental feed)");

        security.MapGet("/audit", async (string? after, int? limit, HttpContext http, GatewayDbContext db, IOptions<DataApiOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var (cursor, size) = Feeds.Window(after, limit, options.Value);
            var rows = await db.AuditLog.AsNoTracking().Where(a => a.Id > cursor).OrderBy(a => a.Id).Take(size + 1)
                .Select(a => new AuditItem(a.Id, a.Timestamp, a.Actor, a.Action, a.EntityType, a.EntityId, a.Details, a.RecordedAt))
                .ToListAsync(ct);
            return DataResults.Feed(http, Feeds.Page(rows, cursor, size, Feeds.Cutoff(time, options.Value)));
        }).WithSummary("Admin audit log with masked before/after details (incremental feed)");

        security.MapGet("/keys", async (HttpContext http, GatewayDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            // Projected column by column: the Data API's database role cannot read KeyHash or EncryptedSecret at all.
            var keys = await db.VirtualKeys.AsNoTracking().OrderBy(k => k.Id).Select(k => new
            {
                k.Id, k.Prefix, k.Name, k.TeamId, DepartmentId = (Guid?)k.Team!.DepartmentId, k.IsEnabled, k.CreatedAt, k.CreatedBy, k.ExpiresAt,
                k.RevokedAt, k.GraceUntil, k.RotatedToKeyId, k.LastUsedAt, k.PiiPolicy, k.AttachmentPolicy, k.AllowedModels, k.AllowedProviders,
                k.AllowedResidencies, k.RequestsPerMinute, k.TokensPerMinute,
            }).ToListAsync(ct);
            return DataResults.List(http, keys.Select(k => new KeyInventoryItem(k.Id, k.Prefix, k.Name, k.TeamId, k.DepartmentId,
                new VirtualKey { Name = k.Name, Prefix = k.Prefix, KeyHash = string.Empty, IsEnabled = k.IsEnabled, ExpiresAt = k.ExpiresAt, RevokedAt = k.RevokedAt, GraceUntil = k.GraceUntil }.GetStatus(now),
                k.CreatedAt, k.CreatedBy, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.RotatedToKeyId, k.LastUsedAt, k.PiiPolicy,
                k.AttachmentPolicy, k.AllowedModels, k.AllowedProviders, k.AllowedResidencies, k.RequestsPerMinute, k.TokensPerMinute)).ToList());
        }).WithSummary("Every key with its status and policies (never the secret or its hash)");

        var catalog = v1.MapGroup("/catalog").WithTags("Catalog").RequireAuthorization(DataPermissions.CatalogRead);
        catalog.MapGet("/departments", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.Departments.AsNoTracking().OrderBy(d => d.Name)
                .Select(d => new { d.Id, d.Name, d.CostCenterCode, d.IsActive, d.CreatedAt }).ToListAsync(ct)));
        catalog.MapGet("/teams", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.Teams.AsNoTracking().OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.DepartmentId, t.Name, t.IsActive, t.CreatedAt }).ToListAsync(ct)));
        catalog.MapGet("/keys", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.VirtualKeys.AsNoTracking().OrderBy(k => k.Id)
                .Select(k => new { k.Id, k.TeamId, k.Name, k.Prefix, k.CreatedAt }).ToListAsync(ct)));
        catalog.MapGet("/providers", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.ProviderAccounts.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name, p.DisplayName, p.Type, p.Residency, p.IsEnabled }).ToListAsync(ct)));
        catalog.MapGet("/models", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.ModelDeployments.AsNoTracking().OrderBy(m => m.Name)
                .Select(m => new { m.Id, m.Name, m.UpstreamModel, m.Kind, ProviderId = m.ProviderAccountId, m.IsEnabled }).ToListAsync(ct)));
        catalog.MapGet("/prices", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.ModelPrices.AsNoTracking().OrderBy(p => p.ModelDeploymentId).ThenBy(p => p.EffectiveFrom)
                .Select(p => new { ModelId = p.ModelDeploymentId, p.EffectiveFrom, p.InputPerMillionUsd, p.CachedInputPerMillionUsd, p.OutputPerMillionUsd, p.AudioPerMinuteUsd, p.AudioInputPerMillionUsd, p.AudioOutputPerMillionUsd }).ToListAsync(ct)));
        catalog.MapGet("/budgets", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.Budgets.AsNoTracking().OrderBy(b => b.Id)
                .Select(b => new { b.Id, b.Scope, b.ScopeId, b.Period, b.LimitSek, b.IsActive }).ToListAsync(ct)));
        catalog.MapGet("/exchange-rates", async (HttpContext http, GatewayDbContext db, CancellationToken ct) => DataResults.List(http,
            await db.ExchangeRates.AsNoTracking().OrderBy(r => r.EffectiveFrom)
                .Select(r => new { r.Currency, r.SekPerUnit, r.EffectiveFrom }).ToListAsync(ct)));
    }

    /// <summary>
    /// Totals per period (in <see cref="DataApiOptions.TimeZone"/>), department, model, provider and endpoint. A department
    /// with fewer than <see cref="DataApiOptions.MinimumGroupSize"/> distinct keys in a period is reported without its
    /// department (all such departments together), so a one-person team's use does not show as its own row.
    /// </summary>
    internal static async Task<List<AggregateItem>> AggregateAsync(GatewayDbContext db, DataApiOptions options, DateOnly from, DateOnly to, string? granularity, CancellationToken ct)
    {
        var unit = (granularity ?? "day").ToLowerInvariant() switch
        {
            "day" => "day",
            "month" => "month",
            _ => throw new BadHttpRequestException("'granularity' must be day or month."),
        };
        if (unit == "month")
        {
            from = new DateOnly(from.Year, from.Month, 1);
            to = new DateOnly(to.Year, to.Month, 1).AddMonths(1).AddDays(-1);
        }

        if (to < from || to.DayNumber - from.DayNumber + 1 > options.MaxAggregateDays)
        {
            throw new BadHttpRequestException($"'to' must be on or after 'from', and the range at most {options.MaxAggregateDays} days.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var start = LocalMidnightUtc(from, zone);
        var end = LocalMidnightUtc(to.AddDays(1), zone);
        var minimum = options.MinimumGroupSize;
        var tz = options.TimeZone;

        var rows = await db.Database.SqlQuery<AggregateRow>($"""
            WITH u AS (
                SELECT date_trunc({unit}, "Timestamp" AT TIME ZONE {tz})::date AS "Period", "DepartmentId", "VirtualKeyId",
                       "ModelDeploymentId", "ProviderAccountId", "Endpoint", "Outcome", "InputTokens", "CachedInputTokens",
                       "OutputTokens", "CostSek", "CostUsd", "AudioSeconds"
                FROM "UsageRecords"
                WHERE "Timestamp" >= {start} AND "Timestamp" < {end}
            ), small AS (
                SELECT "Period", "DepartmentId" FROM u
                GROUP BY "Period", "DepartmentId"
                HAVING count(DISTINCT "VirtualKeyId") < {minimum}
            )
            SELECT u."Period",
                   CASE WHEN small."DepartmentId" IS NULL THEN u."DepartmentId" END AS "DepartmentId",
                   u."ModelDeploymentId" AS "ModelId", u."ProviderAccountId" AS "ProviderId", u."Endpoint",
                   count(*) AS "Requests",
                   count(*) FILTER (WHERE u."Outcome" = 'Success') AS "SuccessfulRequests",
                   sum(u."InputTokens")::bigint AS "InputTokens", sum(u."CachedInputTokens")::bigint AS "CachedInputTokens",
                   sum(u."OutputTokens")::bigint AS "OutputTokens", sum(u."CostSek") AS "CostSek", sum(u."CostUsd") AS "CostUsd",
                   sum(u."AudioSeconds") AS "AudioSeconds"
            FROM u LEFT JOIN small ON small."Period" = u."Period" AND small."DepartmentId" = u."DepartmentId"
            GROUP BY 1, 2, 3, 4, 5
            ORDER BY 1, 2, 3, 4, 5
            """).ToListAsync(ct);

        var departments = await db.Departments.AsNoTracking().Select(d => new { d.Id, d.Name, d.CostCenterCode }).ToDictionaryAsync(d => d.Id, ct);
        var models = await db.ModelDeployments.AsNoTracking().Select(m => new { m.Id, m.Name }).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var providers = await db.ProviderAccounts.AsNoTracking().Select(p => new { p.Id, p.Name, p.Residency }).ToDictionaryAsync(p => p.Id, ct);
        return [.. rows.Select(r =>
        {
            var department = r.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null;
            var provider = r.ProviderId is { } p ? providers.GetValueOrDefault(p) : null;
            return new AggregateItem(r.Period, r.DepartmentId, department?.CostCenterCode, department?.Name, r.ModelId,
                r.ModelId is { } m ? models.GetValueOrDefault(m) : null, r.ProviderId, provider?.Name, provider?.Residency, r.Endpoint,
                r.Requests, r.SuccessfulRequests, r.InputTokens, r.CachedInputTokens, r.OutputTokens, r.CostSek, r.CostUsd, r.AudioSeconds);
        })];
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
