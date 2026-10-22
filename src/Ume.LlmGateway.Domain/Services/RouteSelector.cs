using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>Constraints applied when choosing where a request may go. <c>AllowedResidencies</c>: null = any, empty = none.</summary>
public sealed record RoutingConstraints(
    GatewayEndpoint Endpoint,
    IReadOnlyCollection<DataResidency>? AllowedResidencies,
    Func<Guid, bool>? IsProviderHealthy = null,
    IReadOnlyCollection<string>? AllowedProviders = null);

/// <summary>Chooses which of <paramref name="pool"/> (targets of one priority, at least two) is tried first.</summary>
public delegate int FirstTargetPicker(List<RouteTarget> pool, Random random);

public static class RouteSelector
{
    /// <summary>
    /// Orders targets into an attempt list: ascending priority (fallback chain); within the same priority a
    /// weighted random order (load balancing). Targets violating constraints are removed. With
    /// <paramref name="pickFirst"/> (<see cref="PowerOfTwoChoices"/>, <see cref="Rendezvous"/>) the first target of a
    /// priority is the one it picks; the rest follow in weighted random order.
    /// </summary>
    public static IReadOnlyList<RouteTarget> Order(IEnumerable<RouteTarget> targets, RoutingConstraints constraints, Random random, FirstTargetPicker? pickFirst = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(random);

        var eligible = targets.Where(t => IsEligible(t, constraints)).ToList();
        var result = new List<RouteTarget>(eligible.Count);

        foreach (var group in eligible.GroupBy(t => t.Priority).OrderBy(g => g.Key))
        {
            var pool = group.ToList();
            if (pickFirst is not null && pool.Count > 1)
            {
                var first = pickFirst(pool, random);
                result.Add(pool[first]);
                pool.RemoveAt(first);
            }

            while (pool.Count > 0)
            {
                var index = WeightedIndex(pool, random);
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

    /// <summary>
    /// Power of two choices: the better (higher <paramref name="score"/>) of two targets drawn by weight. Drawn with
    /// replacement, so the worst target still gets some traffic and its averages stay current.
    /// </summary>
    public static FirstTargetPicker PowerOfTwoChoices(Func<RouteTarget, double> score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return (pool, random) =>
        {
            var a = WeightedIndex(pool, random);
            var b = WeightedIndex(pool, random);
            return score(pool[b]) > score(pool[a]) ? b : a;
        };
    }

    /// <summary>
    /// Weighted rendezvous hashing: the same <paramref name="affinity"/> always picks the same target while the set of
    /// targets is unchanged, and a removed target moves only its own share. Keeps a conversation on one server, so the
    /// server's prompt (prefix) cache is reused. Each target wins about its weight's share of affinity values.
    /// </summary>
    public static FirstTargetPicker Rendezvous(ulong affinity) => (pool, _) =>
    {
        var best = 0;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < pool.Count; i++)
        {
            // weight / -ln(u) with u uniform in (0, 1) from the hash: the largest wins (weighted HRW).
            var u = ((Mix(affinity, pool[i].ModelDeploymentId) >> 11) + 0.5) / (1UL << 53);
            var score = Math.Max(pool[i].Weight, 1) / -Math.Log(u);
            if (score > bestScore)
            {
                (best, bestScore) = (i, score);
            }
        }

        return best;
    };

    /// <summary>A stable 64-bit hash of an affinity value (FNV-1a; unlike <see cref="string.GetHashCode()"/> the same in every process).</summary>
    public static ulong AffinityHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hash = 14695981039346656037UL;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong affinity, Guid target)
    {
        Span<byte> bytes = stackalloc byte[16];
        target.TryWriteBytes(bytes);
        var x = affinity ^ BitConverter.ToUInt64(bytes) ^ (BitConverter.ToUInt64(bytes[8..]) * 0x9E3779B97F4A7C15UL);
        // SplitMix64 finaliser.
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Load balancing between targets, not a security decision.")]
    private static int WeightedIndex(List<RouteTarget> pool, Random random)
    {
        var total = 0;
        foreach (var t in pool)
        {
            total += Math.Max(t.Weight, 1);
        }

        var pick = random.Next(total);
        var index = 0;
        for (; index < pool.Count - 1; index++)
        {
            pick -= Math.Max(pool[index].Weight, 1);
            if (pick < 0)
            {
                break;
            }
        }

        return index;
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