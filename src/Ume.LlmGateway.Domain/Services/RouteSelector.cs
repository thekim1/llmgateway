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
        var deployment = target.ModelDeployment;
        var provider = deployment?.ProviderAccount;
        if (deployment is null || provider is null || !deployment.IsEnabled || !provider.IsAvailable)
        {
            return false;
        }

        if (!provider.Supports(constraints.Endpoint))
        {
            return false;
        }

        if (!IsProviderAllowed(provider, constraints.AllowedProviders))
        {
            return false;
        }

        return constraints.AllowedResidencies is null || constraints.AllowedResidencies.Contains(provider.Residency);
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