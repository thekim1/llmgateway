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
        var ops = api.MapGroup("/ops").RequireAuthorization("admin").WithTags("Operations");
        ops.MapGet("/health", async (AdminContext ctx, ProviderStabilityService stability, ICircuitBreakerStore circuits, HealthCheckService health, GatewayOperationsClient gatewayClient, CancellationToken ct) =>
        {
            var checkedAt = ctx.Now;
            var providers = await ctx.Db.ProviderAccounts.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            var mcpServers = await ctx.Db.McpServers.AsNoTracking().OrderBy(s => s.Slug).ToListAsync(ct);
            var waiting = await ctx.Db.McpTools.AsNoTracking()
                .Where(t => t.ApprovalState == McpToolApprovalState.Pending || t.ApprovalState == McpToolApprovalState.Changed)
                .GroupBy(t => new { t.McpServerId, t.ApprovalState }).Select(g => new { g.Key.McpServerId, g.Key.ApprovalState, Count = g.Count() }).ToListAsync(ct);
            var open = await circuits.GetOpenAsync([.. providers.Select(p => p.Id), .. mcpServers.Select(s => s.Id)], ct);
            var stats = (await stability.PeriodAsync(checkedAt.AddDays(-1), checkedAt.AddHours(1), checkedAt - ProviderStabilityService.RecentWindow, ct))
                .ToDictionary(s => s.ProviderId);
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
                    var circuit = open.Contains(p.Id) ? CircuitState.Open : CircuitState.Closed;
                    var (status, reason) = ProviderStabilityService.Classify(p.IsEnabled, circuit, s?.RecentRequests ?? 0, s?.RecentProviderErrors ?? 0);
                    return new ProviderHealthDto(
                        p.Id, p.Name, p.Type, p.Residency, p.IsEnabled, p.IsDrained, circuit,
                        requests, requests == 0 ? 0 : s!.Errors / (double)requests, requests == 0 ? 0 : s!.Fallbacks / (double)requests,
                        s?.P50LatencyMs, s?.P95LatencyMs, requests == 0 ? 0 : s!.ProviderErrors / (double)requests, status, reason,
                        s?.RecentRequests ?? 0, s?.RecentProviderErrors ?? 0);
                })],
                [.. mcpServers.Select(m => new McpServerHealthDto(
                    m.Id, m.Slug, m.DisplayName, m.IsEnabled, m.Status, m.LastCheckedAt, m.LastError, open.Contains(m.Id) ? CircuitState.Open : CircuitState.Closed,
                    waiting.Where(w => w.McpServerId == m.Id && w.ApprovalState == McpToolApprovalState.Pending).Sum(w => w.Count),
                    waiting.Where(w => w.McpServerId == m.Id && w.ApprovalState == McpToolApprovalState.Changed).Sum(w => w.Count)))]));
        }).WithName("GetOperationsHealth").WithSummary("Component health, versions, usage writer state, each provider's status (last hour) and last 24 hours, and each MCP server's last sync");
        ops.MapGet("/providers/stats", async (DateTimeOffset? from, DateTimeOffset? to, string? bucket, AdminContext ctx, ProviderStabilityService stability, CancellationToken ct) =>
            TypedResults.Ok(await stability.StabilityAsync(from, to, bucket, ctx.Now, ct)))
            .WithName("GetProviderStability").WithSummary("Requests, provider errors, fallbacks and latency per provider per hour or day, and a ranking for the period");
        ops.MapPost("/providers/{id:guid}/circuit", async (Guid id, CircuitRequest input, AdminContext ctx, ProviderCatalogService catalog, ClaimsPrincipal user, ICircuitBreakerStore circuits, CancellationToken ct) =>
        {
            _ = await catalog.ProviderAsync(id, ct);
            await circuits.SetStateAsync(id, input.State, TimeSpan.FromMinutes(15), ct);
            await ctx.SaveAsync(user, AuditActions.Circuit, AuditEntities.Provider, id, null, new CircuitAudit(input.State), InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).WithName("SetProviderCircuit").WithSummary("Opens or closes a provider's circuit for 15 minutes");
        ops.MapPost("/mcp-servers/{id:guid}/circuit", async (Guid id, CircuitRequest input, AdminContext ctx, ClaimsPrincipal user, ICircuitBreakerStore circuits, CancellationToken ct) =>
        {
            if (!await ctx.Db.McpServers.AnyAsync(s => s.Id == id, ct)) { throw new ApiFaultException(404, "MCP-servern finns inte."); }
            await circuits.SetStateAsync(id, input.State, TimeSpan.FromMinutes(15), ct);
            await ctx.SaveAsync(user, AuditActions.Circuit, AuditEntities.McpServer, id, null, new CircuitAudit(input.State), InvalidationKind.Config, ct);
            return TypedResults.NoContent();
        }).WithName("SetMcpServerCircuit").WithSummary("Opens or closes an MCP server's circuit for 15 minutes (open hides its tools)");
        // A deliberate operator action, so it is audited (unlike the invalidations that accompany saved changes).
        ops.MapPost("/cache/invalidate-keys", async (AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await ctx.SaveAsync(user, AuditActions.Invalidate, AuditEntities.Keys, "all", null, null, InvalidationKind.Keys, ct);
            return TypedResults.NoContent();
        }).WithName("InvalidateKeyCaches").WithSummary("Makes every gateway reload keys");
        ops.MapGet("/config/export", async (ConfigTransferService transfer, CancellationToken ct) => TypedResults.Ok(await transfer.ExportAsync(ct)))
            .WithName("ExportConfiguration").WithSummary("Providers and MCP servers (without secrets), models, prices, routes, global routing rules and approved MCP tools");
        ops.MapPost("/config/import", async (ConfigDocument input, ConfigTransferService transfer, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await transfer.ImportAsync(input, user, ct)))
            .WithName("ImportConfiguration").WithSummary("Creates or updates everything in an exported document, in one transaction");
    }
}
