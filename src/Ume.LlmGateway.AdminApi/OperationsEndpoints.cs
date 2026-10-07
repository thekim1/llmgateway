using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record CircuitRequest([property: EnumDataType(typeof(CircuitState))] CircuitState State) : AdminRequest;

public static class OperationsEndpoints
{
    public static void MapOperations(this RouteGroupBuilder api)
    {
        var ops = api.MapGroup("/ops").RequireAuthorization("admin");
        ops.MapGet("/health", async (AdminContext ctx, ICircuitBreakerStore circuits, HealthCheckService health, GatewayOperationsClient gatewayClient, CancellationToken ct) =>
        {
            var checkedAt = ctx.Now;
            var providers = await ctx.Db.ProviderAccounts.AsNoTracking().ToListAsync(ct);
            var open = await circuits.GetOpenAsync(providers.Select(p => p.Id).ToArray(), ct);
            var since = checkedAt.AddDays(-1);
            var usage = await ctx.Db.UsageRecords.AsNoTracking().Where(u => u.Timestamp >= since && u.ProviderAccountId != null)
                .Select(u => new { u.ProviderAccountId, u.Outcome, u.FallbackCount, u.LatencyMs }).ToListAsync(ct);
            var report = await health.CheckHealthAsync(ct);
            var schema = (await ctx.Db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "none";
            var gateway = await gatewayClient.ReadAsync(ct);
            return Results.Ok(new
            {
                checkedAt,
                components = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Status == HealthStatus.Healthy ? "OK" : "Kontrollera tjänstens anslutning." })
                    .Append(new { name = "gateway", status = gateway?.Status ?? "Unhealthy", description = gateway is null ? "Gatewayens hälsa kunde inte hämtas." : gateway.Status == "Healthy" ? "OK" : "Kontrollera gatewayens anslutningar eller användningskö." }),
                versions = new { adminApi = typeof(OperationsEndpoints).Assembly.GetName().Version?.ToString(), gateway = gateway?.Version, schema },
                usageWriter = gateway?.UsageWriter,
                providers = providers.Select(p =>
                {
                    var records = usage.Where(u => u.ProviderAccountId == p.Id).ToArray();
                    var latencies = records.Select(u => u.LatencyMs).Order().ToArray();
                    return new
                    {
                        p.Id, p.Name, p.Type, p.Residency, p.IsEnabled, p.IsDrained,
                        circuitState = open.Contains(p.Id) ? CircuitState.Open : CircuitState.Closed,
                        requests24h = records.Length,
                        errorRate24h = records.Length == 0 ? 0 : records.Count(u => u.Outcome != RequestOutcome.Success) / (double)records.Length,
                        fallbackRate24h = records.Length == 0 ? 0 : records.Count(u => u.FallbackCount > 0) / (double)records.Length,
                        p50LatencyMs = Percentile(latencies, 0.5), p95LatencyMs = Percentile(latencies, 0.95),
                    };
                }),
            });
        });
        ops.MapPost("/providers/{id:guid}/circuit", async (Guid id, CircuitRequest input, AdminContext ctx, ClaimsPrincipal user, ICircuitBreakerStore circuits, CancellationToken ct) =>
        {
            _ = await ConfigurationEndpoints.ProviderAsync(ctx, id, ct);
            await circuits.SetStateAsync(id, input.State, TimeSpan.FromMinutes(15), ct);
            await ctx.SaveAsync(user, "circuit", "Provider", id, null, new { input.State }, InvalidationKind.Config, ct);
            return Results.NoContent();
        });
        ops.MapPost("/cache/invalidate-keys", async (AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await ctx.SaveAsync(user, "invalidate", "Keys", "all", null, null, InvalidationKind.Keys, ct);
            return Results.NoContent();
        });
        ops.MapGet("/config/export", ConfigTransfer.ExportAsync);
        ops.MapPost("/config/import", ConfigTransfer.ImportAsync);
    }
    private static int? Percentile(int[] sorted, double percentile) =>
        sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
}
