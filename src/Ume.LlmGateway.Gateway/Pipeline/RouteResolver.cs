using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>A request that routing refuses, with the HTTP status and client-facing error to return.</summary>

/// <summary>
/// Outcome of resolving the model name the client asked for. When both <c>Model</c> and <c>Rejection</c> are null the
/// name is unknown but routing rules exist that may rewrite it, so the decision is deferred until they have run.
/// </summary>
public sealed record ModelResolution(ResolvedModel? Model, GatewayError? Rejection);

/// <summary>The models a request may be sent to, in attempt order, or a rejection.</summary>
public sealed record RoutePlan(IReadOnlyList<ResolvedModel> Models, GatewayError? Rejection);

/// <summary>Outcome of candidate selection: the ordered attempt list, or a rejection when none is eligible.</summary>
public sealed record CandidateSelection(IReadOnlyList<RouteTarget> Candidates, GatewayError? Rejection);

/// <summary>A circuit-state lookup started early (see <see cref="IRouteResolver.PrefetchCircuits"/>) for these providers.</summary>
public sealed record CircuitPrefetch(IReadOnlySet<Guid> ProviderIds, Task<IReadOnlySet<Guid>> Open);

/// <summary>
/// Decides where a request may go, in three steps: resolve the requested model name and check the key's allow-lists
/// and endpoint compatibility; turn a routing-rule decision (if any) into the models to use; order the eligible
/// targets of those models into an attempt list. Rules only choose among destinations: provider allow-lists,
/// residency, PII restriction and provider health are applied afterwards to whatever the rules chose.
/// </summary>
public interface IRouteResolver
{
    ModelResolution ResolveModel(CatalogSnapshot snapshot, VirtualKey key, GatewayEndpoint endpoint, string model);

    RoutePlan Plan(CatalogSnapshot snapshot, VirtualKey key, GatewayEndpoint endpoint, string requestedModel, ResolvedModel? requested, RoutingDecision decision);

    /// <summary>
    /// Starts looking up which of the model's providers have an open circuit, so the round trip overlaps the rest of
    /// the pipeline. <see cref="SelectCandidatesAsync"/> uses it when it covers the providers finally considered.
    /// </summary>
    CircuitPrefetch PrefetchCircuits(ResolvedModel model, CancellationToken ct);

    Task<CandidateSelection> SelectCandidatesAsync(IReadOnlyList<ResolvedModel> models, VirtualKey key, GatewayEndpoint endpoint, DataResidency? restrictTo, bool stream, CancellationToken ct, CircuitPrefetch? prefetch = null);
}

public sealed class RouteResolver(ICircuitBreakerStore circuits) : IRouteResolver
{
    public ModelResolution ResolveModel(CatalogSnapshot snapshot, VirtualKey key, GatewayEndpoint endpoint, string model)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(model);

        var resolved = snapshot.Resolve(model);
        if (resolved is null)
        {
            // A rule may rewrite an unknown (e.g. legacy) or ambiguous name; without rules it is refused.
            return snapshot.Rules.Count > 0 ? new ModelResolution(null, null)
                : snapshot.AmbiguousChoices(model) is { } choices ? Reject(GatewayError.ModelAmbiguous(model, choices))
                : Reject(GatewayError.ModelNotFound(model));
        }

        if (!resolved.IsAllowedFor(key))
        {
            return Reject(GatewayError.ModelNotAllowed(resolved.Name));
        }

        if (key.AllowedProviders.Count > 0
            && !resolved.Targets.Any(t => t.ModelDeployment?.ProviderAccount is { } p && RouteSelector.IsProviderAllowed(p, key.AllowedProviders)))
        {
            return Reject(GatewayError.NoAllowedProvider(resolved.Name));
        }

        if (!GatewayEndpoints.Info(endpoint).Serves(resolved.Kind))
        {
            return Reject(GatewayError.EndpointNotSupported(resolved.Name));
        }

        return new ModelResolution(resolved, null);
    }

    public RoutePlan Plan(CatalogSnapshot snapshot, VirtualKey key, GatewayEndpoint endpoint, string requestedModel, ResolvedModel? requested, RoutingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.Matched)
        {
            return requested is not null ? new RoutePlan([requested], null)
                : snapshot.AmbiguousChoices(requestedModel) is { } choices ? Rejected(GatewayError.ModelAmbiguous(requestedModel, choices))
                : Rejected(GatewayError.ModelNotFound(requestedModel));
        }

        if (requested is null)
        {
            // The client used a name that only a rule knows. The key is entitled to what that name stands for: the
            // first model in the rule chain that exists. (A known name is checked as requested, in ResolveModel.)
            var entitled = decision.Applied.Select(a => snapshot.Resolve(a.ToModel)).FirstOrDefault(r => r is not null);
            if (entitled is null)
            {
                return Rejected(GatewayError.ModelNotFound(requestedModel));
            }

            if (!entitled.IsAllowedFor(key))
            {
                return Rejected(GatewayError.ModelNotAllowed(entitled.Name));
            }
        }

        var info = GatewayEndpoints.Info(endpoint);
        var models = decision.Models
            .Select(snapshot.Resolve)
            .Where(m => m is not null && info.Serves(m.Kind))
            .Select(m => m!)
            .ToList();
        return models.Count > 0
            ? new RoutePlan(models, null)
            : Rejected(GatewayError.RuleTargetsUnusable(requestedModel));
    }

    public CircuitPrefetch PrefetchCircuits(ResolvedModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var ids = ProviderIds([model]);
        var open = circuits.GetOpenAsync(ids, ct);
        // The request may be rejected before the result is needed; never leave a failure unobserved.
        _ = open.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return new CircuitPrefetch(ids, open);
    }

    public async Task<CandidateSelection> SelectCandidatesAsync(IReadOnlyList<ResolvedModel> models, VirtualKey key, GatewayEndpoint endpoint, DataResidency? restrictTo, bool stream, CancellationToken ct, CircuitPrefetch? prefetch = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(key);

        var residencies = RouteSelector.EffectiveResidencies(key.AllowedResidencies, restrictTo);
        var providerIds = ProviderIds(models);
        var open = prefetch is not null && prefetch.ProviderIds.IsSupersetOf(providerIds)
            ? await prefetch.Open.WaitAsync(ct)
            : await circuits.GetOpenAsync(providerIds, ct);
        var constraints = new RoutingConstraints(endpoint, residencies, null, key.AllowedProviders);

        // Each model keeps its own priority/weight order; models follow each other in the order the rules gave.
        var ordered = new List<RouteTarget>();
        var seen = new HashSet<Guid>();
        foreach (var model in models)
        {
            foreach (var target in RouteSelector.Order(model.Targets, constraints, Random.Shared))
            {
                if (models.Count == 1 || seen.Add(target.ModelDeploymentId))
                {
                    ordered.Add(target);
                }
            }
        }

        // Providers whose circuit is open go last (not removed), across all models, so a healthy fallback model is tried first.
        var candidates = new List<RouteTarget>(ordered.Count);
        candidates.AddRange(ordered.Where(t => !open.Contains(t.ModelDeployment!.ProviderAccountId)));
        candidates.AddRange(ordered.Where(t => open.Contains(t.ModelDeployment!.ProviderAccountId)));
        candidates = [.. candidates.Where(t => !stream || t.ModelDeployment!.ProviderAccount!.Capabilities.HasFlag(ProviderCapabilities.Streaming))];
        if (candidates.Count > 0)
        {
            return new CandidateSelection(candidates, null);
        }

        return new CandidateSelection(candidates, GatewayError.NoEligibleProvider(onPremOnly: restrictTo == DataResidency.OnPrem));
    }

    private static HashSet<Guid> ProviderIds(IEnumerable<ResolvedModel> models) =>
        [.. models.SelectMany(m => m.Targets).Where(t => t.ModelDeployment is not null).Select(t => t.ModelDeployment!.ProviderAccountId)];

    private static ModelResolution Reject(GatewayError error) => new(null, error);

    private static RoutePlan Rejected(GatewayError error) => new([], error);
}
