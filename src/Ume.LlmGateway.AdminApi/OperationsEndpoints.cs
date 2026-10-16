using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record CircuitRequest([property: EnumDataType(typeof(CircuitState))] CircuitState State) : AdminRequest;

public static class OperationsEndpoints
{
    public static void MapOperations(this RouteGroupBuilder api)
    {
        var ops = api.MapGroup("/ops").RequireAuthorization("admin").WithTags("Operations");
        ops.MapGet("/health", async (AdminContext ctx, ProviderStabilityService stability, ICircuitBreakerStore circuits, HealthCheckService health, GatewayOperationsClient gatewayClient, CancellationToken ct) =>
        {
            var checkedAt = ctx.Now;
            var providers = await ctx.Db.ProviderAccounts.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            var open = await circuits.GetOpenAsync(providers.Select(p => p.Id).ToArray(), ct);
            var stats = (await stability.PeriodAsync(checkedAt.AddDays(-1), checkedAt.AddHours(1), checkedAt - ProviderStabilityService.RecentWindow, ct))
                .ToDictionary(s => s.ProviderId);
            var report = await health.CheckHealthAsync(ct);
            var schema = (await ctx.Db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "none";
            var gateway = await gatewayClient.ReadAsync(ct);
            ComponentHealthDto[] components =
            [
                .. report.Entries.Select(e => new ComponentHealthDto(e.Key, e.Value.Status.ToString(), e.Value.Status == HealthStatus.Healthy ? "OK" : "Check the service's connection.")),
                new ComponentHealthDto("gateway", gateway?.Status ?? "Unhealthy",
                    gateway is null ? "The gateway's health could not be fetched." : gateway.Status == "Healthy" ? "OK" : "Check the gateway's connections or usage queue."),
            ];
            return TypedResults.Ok(new OpsHealthDto(
                checkedAt, components,
                new VersionsDto(typeof(OperationsEndpoints).Assembly.GetName().Version?.ToString(), gateway?.Version, schema),
                gateway?.UsageWriter,
                [.. providers.Select(p =>
                {
                    var s = stats.GetValueOrDefault(p.Id);
                    var requests = s?.Requests ?? 0;
                    var circuit = open.Contains(p.Id) ? CircuitState.Open : CircuitState.Closed;
                    var (status, reason) = ProviderStabilityService.Classify(p.IsEnabled, circuit, s?.RecentRequests ?? 0, s?.RecentProviderErrors ?? 0);
                    return new ProviderHealthDto(
                        p.Id, p.Name, p.Type, p.Residency, p.IsEnabled, p.IsDrained, circuit,
                        requests, requests == 0 ? 0 : s!.Errors / (double)requests, requests == 0 ? 0 : s!.Fallbacks / (double)requests,
                        s?.P50LatencyMs, s?.P95LatencyMs, requests == 0 ? 0 : s!.ProviderErrors / (double)requests, status, reason,
                        s?.RecentRequests ?? 0, s?.RecentProviderErrors ?? 0);
                })]));
        }).WithName("GetOperationsHealth").WithSummary("Component health, versions, usage writer state, each provider's status (last hour) and last 24 hours");
        ops.MapGet("/providers/stats", async (DateTimeOffset? from, DateTimeOffset? to, string? bucket, AdminContext ctx, ProviderStabilityService stability, CancellationToken ct) =>
            TypedResults.Ok(await stability.StabilityAsync(from, to, bucket, ctx.Now, ct)))
            .WithName("GetProviderStability").WithSummary("Requests, provider errors, fallbacks and latency per provider per hour or day, and a ranking for the period");
        ops.MapPost("/providers/{id:guid}/circuit", async (Guid id, CircuitRequest input, AdminContext ctx, ProviderCatalogService catalog, ICircuitBreakerStore circuits, CancellationToken ct) =>
        {
            _ = await catalog.ProviderAsync(id, ct);
            await circuits.SetStateAsync(id, input.State, TimeSpan.FromMinutes(15), ct);
            await ctx.SaveAsync(AuditActions.Circuit, AuditEntities.Provider, id, null, new CircuitAudit(input.State), InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).WithName("SetProviderCircuit").WithSummary("Opens or closes a provider's circuit for 15 minutes");
        // A deliberate operator action, so it is audited (unlike the invalidations that accompany saved changes).
        ops.MapPost("/cache/invalidate-keys", async (AdminContext ctx, CancellationToken ct) =>
        {
            await ctx.SaveAsync(AuditActions.Invalidate, AuditEntities.Keys, "all", null, null, InvalidationKind.Keys, ct);
            return TypedResults.NoContent();
        }).WithName("InvalidateKeyCaches").WithSummary("Makes every gateway reload keys");
        ops.MapGet("/config/export", async (ConfigTransferService transfer, CancellationToken ct) => TypedResults.Ok(await transfer.ExportAsync(ct)))
            .WithName("ExportConfiguration").WithSummary("Providers (without secrets), models, prices, routes and global routing rules");
        ops.MapPost("/config/import", async (ConfigDocument input, ConfigTransferService transfer, CancellationToken ct) =>
            TypedResults.Ok(await transfer.ImportAsync(input, ct)))
            .WithName("ImportConfiguration").WithSummary("Creates or updates everything in an exported document, in one transaction");
    }
}
