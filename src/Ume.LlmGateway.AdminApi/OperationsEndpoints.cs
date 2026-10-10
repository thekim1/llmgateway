using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record CircuitRequest([property: EnumDataType(typeof(CircuitState))] CircuitState State) : AdminRequest;

/// <summary>A provider's requests in a period, aggregated in the database.</summary>
public sealed class ProviderUsageStats
{
    public Guid ProviderId { get; set; }
    public long Requests { get; set; }
    public long Errors { get; set; }
    public long Fallbacks { get; set; }
    public int? P50LatencyMs { get; set; }
    public int? P95LatencyMs { get; set; }
}

public static class OperationsEndpoints
{
    public static void MapOperations(this RouteGroupBuilder api)
    {
        var ops = api.MapGroup("/ops").RequireAuthorization("admin").WithTags("Operations");
        ops.MapGet("/health", async (AdminContext ctx, ICircuitBreakerStore circuits, HealthCheckService health, GatewayOperationsClient gatewayClient, CancellationToken ct) =>
        {
            var checkedAt = ctx.Now;
            var providers = await ctx.Db.ProviderAccounts.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            var open = await circuits.GetOpenAsync(providers.Select(p => p.Id).ToArray(), ct);
            var stats = (await ProviderStatsAsync(ctx.Db, checkedAt.AddDays(-1), ct)).ToDictionary(s => s.ProviderId);
            var report = await health.CheckHealthAsync(ct);
            var schema = (await ctx.Db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "none";
            var gateway = await gatewayClient.ReadAsync(ct);
            ComponentHealthDto[] components =
            [
                .. report.Entries.Select(e => new ComponentHealthDto(e.Key, e.Value.Status.ToString(), e.Value.Status == HealthStatus.Healthy ? "OK" : "Kontrollera tjänstens anslutning.")),
                new ComponentHealthDto("gateway", gateway?.Status ?? "Unhealthy",
                    gateway is null ? "Gatewayens hälsa kunde inte hämtas." : gateway.Status == "Healthy" ? "OK" : "Kontrollera gatewayens anslutningar eller användningskö."),
            ];
            return TypedResults.Ok(new OpsHealthDto(
                checkedAt, components,
                new VersionsDto(typeof(OperationsEndpoints).Assembly.GetName().Version?.ToString(), gateway?.Version, schema),
                gateway?.UsageWriter,
                [.. providers.Select(p =>
                {
                    var s = stats.GetValueOrDefault(p.Id);
                    var requests = s?.Requests ?? 0;
                    return new ProviderHealthDto(
                        p.Id, p.Name, p.Type, p.Residency, p.IsEnabled, p.IsDrained, open.Contains(p.Id) ? CircuitState.Open : CircuitState.Closed,
                        requests, requests == 0 ? 0 : s!.Errors / (double)requests, requests == 0 ? 0 : s!.Fallbacks / (double)requests,
                        s?.P50LatencyMs, s?.P95LatencyMs);
                })]));
        }).WithName("GetOperationsHealth").WithSummary("Component health, versions, usage writer state and each provider's last 24 hours");
        ops.MapPost("/providers/{id:guid}/circuit", async (Guid id, CircuitRequest input, AdminContext ctx, ProviderCatalogService catalog, ClaimsPrincipal user, ICircuitBreakerStore circuits, CancellationToken ct) =>
        {
            _ = await catalog.ProviderAsync(id, ct);
            await circuits.SetStateAsync(id, input.State, TimeSpan.FromMinutes(15), ct);
            await ctx.SaveAsync(user, AuditActions.Circuit, AuditEntities.Provider, id, null, new CircuitAudit(input.State), InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).WithName("SetProviderCircuit").WithSummary("Opens or closes a provider's circuit for 15 minutes");
        // A deliberate operator action, so it is audited (unlike the invalidations that accompany saved changes).
        ops.MapPost("/cache/invalidate-keys", async (AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await ctx.SaveAsync(user, AuditActions.Invalidate, AuditEntities.Keys, "all", null, null, InvalidationKind.Keys, ct);
            return TypedResults.NoContent();
        }).WithName("InvalidateKeyCaches").WithSummary("Makes every gateway reload keys");
        ops.MapGet("/config/export", async (ConfigTransferService transfer, CancellationToken ct) => TypedResults.Ok(await transfer.ExportAsync(ct)))
            .WithName("ExportConfiguration").WithSummary("Providers (without secrets), models, prices, routes and global routing rules");
        ops.MapPost("/config/import", async (ConfigDocument input, ConfigTransferService transfer, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await transfer.ImportAsync(input, user, ct)))
            .WithName("ImportConfiguration").WithSummary("Creates or updates everything in an exported document, in one transaction");
    }

    /// <summary>
    /// Requests, errors, fallbacks and latency percentiles per provider since <paramref name="since"/>, aggregated in SQL.
    /// A live audio session's "latency" is its length; it would swamp the percentiles of ordinary requests, so realtime
    /// sessions count as requests but not in the percentiles. <c>percentile_disc</c> picks the smallest latency at or above
    /// the percentile, as the nearest-rank method does.
    /// </summary>
    public static Task<List<ProviderUsageStats>> ProviderStatsAsync(GatewayDbContext db, DateTimeOffset since, CancellationToken ct)
    {
        var success = nameof(RequestOutcome.Success);
        var realtime = nameof(GatewayEndpoint.Realtime);
        var realtimeTranslations = nameof(GatewayEndpoint.RealtimeTranslations);
        return db.Database.SqlQuery<ProviderUsageStats>($"""
            SELECT "ProviderAccountId" AS "ProviderId",
                   count(*) AS "Requests",
                   count(*) FILTER (WHERE "Outcome" <> {success}) AS "Errors",
                   count(*) FILTER (WHERE "FallbackCount" > 0) AS "Fallbacks",
                   percentile_disc(0.5) WITHIN GROUP (ORDER BY "LatencyMs") FILTER (WHERE "Endpoint" NOT IN ({realtime}, {realtimeTranslations})) AS "P50LatencyMs",
                   percentile_disc(0.95) WITHIN GROUP (ORDER BY "LatencyMs") FILTER (WHERE "Endpoint" NOT IN ({realtime}, {realtimeTranslations})) AS "P95LatencyMs"
            FROM "UsageRecords"
            WHERE "Timestamp" >= {since} AND "ProviderAccountId" IS NOT NULL
            GROUP BY "ProviderAccountId"
            """).ToListAsync(ct);
    }
}
