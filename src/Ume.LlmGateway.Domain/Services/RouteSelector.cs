using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>Constraints applied when choosing where a request may go. <c>AllowedResidencies</c>: null = any, empty = none.</summary>
public sealed record RoutingConstraints(
    GatewayEndpoint Endpoint,
    IReadOnlyCollection<DataResidency>? AllowedResidencies,
    Func<Guid, bool>? IsProviderHealthy = null,
    IReadOnlyCollection<string>? AllowedProviders = null);

public static class RouteSelector
{
    /// <summary>
    /// Orders targets into an attempt list: ascending priority (fallback chain); within the same priority a
    /// weighted random order (load balancing). Targets violating constraints are removed.
    /// </summary>
    public static IReadOnlyList<RouteTarget> Order(IEnumerable<RouteTarget> targets, RoutingConstraints constraints, Random random)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(random);

        var eligible = targets.Where(t => IsEligible(t, constraints)).ToList();
        var result = new List<RouteTarget>(eligible.Count);

        foreach (var group in eligible.GroupBy(t => t.Priority).OrderBy(g => g.Key))
        {
            var pool = group.ToList();
            while (pool.Count > 0)
            {
                var total = pool.Sum(t => Math.Max(t.Weight, 1));
                var pick = random.Next(total);
                var index = 0;
                for (; index < pool.Count; index++)
                {
                    pick -= Math.Max(pool[index].Weight, 1);
                    if (pick < 0)
                    {
                        break;
                    }
                }

                result.Add(pool[index]);
                pool.RemoveAt(index);
            }
        }

        // Unhealthy (circuit open) providers are moved last instead of removed, so a request still gets
        // a chance if everything else fails.
        if (constraints.IsProviderHealthy is { } healthy)
        {
            result = [.. result.Where(t => healthy(t.ModelDeployment!.ProviderAccountId)),
                      .. result.Where(t => !healthy(t.ModelDeployment!.ProviderAccountId))];
        }

        return result;
    }

    public static bool IsEligible(RouteTarget target, RoutingConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(constraints);
        return IsEligible(target.ModelDeployment, constraints);
    }

    /// <summary>
    /// The one eligibility rule: the deployment and its provider are enabled (and not drained), the provider serves the
    /// endpoint, and the key's provider and residency allow-lists admit it.
    /// </summary>
    public static bool IsEligible(ModelDeployment? deployment, RoutingConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        return IsUsable(deployment, constraints.AllowedResidencies, constraints.AllowedProviders) && deployment!.ProviderAccount!.Supports(constraints.Endpoint);
    }

    /// <summary>
    /// Eligible for at least one endpoint that serves the deployment's kind: what <c>GET /v1/models</c> lists. The
    /// endpoint in <paramref name="constraints"/> is not used.
    /// </summary>
    public static bool IsEligibleForAnyEndpoint(ModelDeployment? deployment, ModelKind kind, RoutingConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        if (!IsUsable(deployment, constraints.AllowedResidencies, constraints.AllowedProviders))
        {
            return false;
        }

        foreach (var endpoint in GatewayEndpoints.ServingKind(kind))
        {
            if (deployment!.ProviderAccount!.Supports(endpoint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUsable(ModelDeployment? deployment, IReadOnlyCollection<DataResidency>? residencies, IReadOnlyCollection<string>? providers)
    {
        var provider = deployment?.ProviderAccount;
        if (deployment is null || provider is null || !deployment.IsEnabled || !provider.IsAvailable)
        {
            return false;
        }

        if (!IsProviderAllowed(provider, providers))
        {
            return false;
        }

        return residencies is null || residencies.Contains(provider.Residency);
    }

    /// <summary>A key's provider allow-list: null or empty = any provider.</summary>
    public static bool IsProviderAllowed(ProviderAccount provider, IReadOnlyCollection<string>? allowed)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return allowed is null || allowed.Count == 0 || allowed.Contains(provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combines the key's residency allow-list (empty = any) with an optional extra restriction
    /// (e.g. PII ? on-prem). Returns null for "any", otherwise the allowed set (possibly empty = nothing allowed).
    /// </summary>
    public static IReadOnlyCollection<DataResidency>? EffectiveResidencies(IReadOnlyCollection<DataResidency> keyAllowed, DataResidency? restrictTo)
    {
        ArgumentNullException.ThrowIfNull(keyAllowed);
        if (restrictTo is not { } only)
        {
            return keyAllowed.Count == 0 ? null : keyAllowed;
        }

        // A restriction must never widen access.
        return keyAllowed.Count == 0 || keyAllowed.Contains(only) ? [only] : [];
    }
}