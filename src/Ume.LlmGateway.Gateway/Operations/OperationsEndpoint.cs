using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Gateway.Usage;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Operations;

public static class OperationsEndpoint
{
    /// <summary>
    /// <see cref="OperationsSignature.Path"/>: health, version and usage writer state for the admin API's operations page,
    /// signed with the key pepper (<see cref="OperationsSignature"/>). Not in the OpenAPI document.
    /// </summary>
    public static void MapGatewayOperations(this IEndpointRouteBuilder app) =>
        app.MapGet(OperationsSignature.Path, async (HttpContext http, IOptions<GatewaySecurityOptions> security,
            TimeProvider time, UsageWriter writer, HealthCheckService health, CancellationToken ct) =>
        {
            if (!OperationsSignature.Verify(security.Value.KeyPepper, http.Request.Headers[OperationsSignature.TimestampHeader].ToString(),
                http.Request.Headers[OperationsSignature.SignatureHeader].ToString(), time.GetUtcNow()))
            {
                return Results.Unauthorized();
            }
            http.Response.Headers.CacheControl = "no-store";
            var report = await health.CheckHealthAsync(ct);
            var status = writer.Status;
            return Results.Ok(new GatewayOperationsStatus(
                status.ConsecutiveFailures > 0 && report.Status == HealthStatus.Healthy ? "Degraded" : report.Status.ToString(),
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown", status));
        }).ExcludeFromDescription();
}
