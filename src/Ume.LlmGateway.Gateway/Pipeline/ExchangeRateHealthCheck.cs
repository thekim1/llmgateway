using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Readiness: Degraded while no USD exchange rate is in effect, because every cost is then calculated with the
/// configured fallback (<c>Gateway:FallbackSekPerUsd</c>) instead of a real rate. Uses the cached catalogue snapshot.
/// </summary>
public sealed class ExchangeRateHealthCheck(GatewayCatalog catalog) : IHealthCheck
{
    public const string Name = "exchange-rate";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = await catalog.GetAsync(cancellationToken);
        return snapshot.UsesFallbackRate
            ? HealthCheckResult.Degraded("No USD exchange rate is in effect; costs use Gateway:FallbackSekPerUsd.")
            : HealthCheckResult.Healthy();
    }
}
