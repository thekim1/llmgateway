using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Security;

namespace Ume.LlmGateway.AdminApi.Operations;

/// <summary>Operations: health, provider stability and circuits, gateway caches and the configuration document. Gateway admins only.</summary>
public static class OperationsEndpoints
{
    public static void MapOperations(this RouteGroupBuilder api)
    {
        var ops = api.MapGroup("/ops").RequireAuthorization(AdminPolicies.Admin).WithTags("Operations");
        ops.MapGet("/health", async (OperationsService service, CancellationToken ct) => TypedResults.Ok(await service.HealthAsync(ct)))
            .WithName("GetOperationsHealth").WithSummary("Component health, versions, usage writer state, each provider's status (last hour) and last 24 hours");
        ops.MapGet("/providers/stats", async (DateTimeOffset? from, DateTimeOffset? to, string? bucket, AdminContext ctx, ProviderStabilityService stability, CancellationToken ct) =>
            TypedResults.Ok(await stability.StabilityAsync(from, to, bucket, ctx.Now, ct)))
            .WithName("GetProviderStability").WithSummary("Requests, provider errors, fallbacks and latency per provider per hour or day, and a ranking for the period");
        ops.MapPost("/providers/{id:guid}/circuit", async (Guid id, CircuitRequest input, OperationsService service, CancellationToken ct) =>
        {
            await service.SetCircuitAsync(id, input.State, ct);
            return TypedResults.NoContent();
        }).WithName("SetProviderCircuit").WithSummary("Opens or closes a provider's circuit for 15 minutes");
        ops.MapPost("/cache/invalidate-keys", async (OperationsService service, CancellationToken ct) =>
        {
            await service.InvalidateKeyCachesAsync(ct);
            return TypedResults.NoContent();
        }).WithName("InvalidateKeyCaches").WithSummary("Makes every gateway reload keys");
        ops.MapGet("/config/export", async (ConfigTransferService transfer, CancellationToken ct) => TypedResults.Ok(await transfer.ExportAsync(ct)))
            .WithName("ExportConfiguration").WithSummary("Providers (without secrets), models, prices, routes and global routing rules");
        ops.MapPost("/config/import", async (ConfigDocument input, ConfigTransferService transfer, CancellationToken ct) =>
            TypedResults.Ok(await transfer.ImportAsync(input, ct)))
            .WithName("ImportConfiguration").WithSummary("Creates or updates everything in an exported document, in one transaction");
    }
}
