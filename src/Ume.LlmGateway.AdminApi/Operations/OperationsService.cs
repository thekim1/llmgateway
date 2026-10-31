using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Catalog;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Operations;

/// <summary>The operations view: component and provider health, provider circuits and gateway caches. Gateway admins only.</summary>
public sealed class OperationsService(
    AdminContext ctx, ProviderStabilityService stability, ProviderCatalogService catalog, ICircuitBreakerStore circuits, HealthCheckService health,
    GatewayOperationsClient gatewayClient)
{
    /// <summary>How long an operator's circuit decision holds before the breaker decides again.</summary>
    public static readonly TimeSpan ManualCircuitDuration = TimeSpan.FromMinutes(15);

    /// <summary>Component health, versions, the gateway's usage writer, and each provider's status (last hour) and last 24 hours.</summary>
    public async Task<OpsHealthDto> HealthAsync(CancellationToken ct)
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
        return new OpsHealthDto(
            checkedAt, components,
            new VersionsDto(typeof(OperationsService).Assembly.GetName().Version?.ToString(), gateway?.Version, schema),
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
                    s?.P50FirstTokenMs, s?.P95FirstTokenMs, requests == 0 ? 0 : s!.ProviderErrors / (double)requests, status, reason,
                    s?.RecentRequests ?? 0, s?.RecentProviderErrors ?? 0);
            })]);
    }

    /// <summary>Opens or closes a provider's circuit for <see cref="ManualCircuitDuration"/>.</summary>
    public async Task SetCircuitAsync(Guid providerId, CircuitState state, CancellationToken ct)
    {
        _ = await catalog.ProviderAsync(providerId, ct);
        await circuits.SetStateAsync(providerId, state, ManualCircuitDuration, ct);
        await ctx.SaveAsync(AuditActions.Circuit, AuditEntities.Provider, providerId, null, new CircuitAudit(state), InvalidationKind.Config, ct);
    }

    /// <summary>
    /// Makes every gateway reload keys. A deliberate operator action, so it is audited (unlike the invalidations that
    /// accompany saved changes).
    /// </summary>
    public Task InvalidateKeyCachesAsync(CancellationToken ct) =>
        ctx.SaveAsync(AuditActions.Invalidate, AuditEntities.Keys, "all", null, null, InvalidationKind.Keys, ct);
}
