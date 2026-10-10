using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>A request that routing refuses, with the HTTP status and client-facing error to return.</summary>
public sealed record RouteRejection(int StatusCode, string Code, string Message);

/// <summary>
/// Outcome of resolving the model name the client asked for. When both <c>Model</c> and <c>Rejection</c> are null the
/// name is unknown but routing rules exist that may rewrite it, so the decision is deferred until they have run.
/// </summary>
public sealed record ModelResolution(ResolvedModel? Model, RouteRejection? Rejection);

/// <summary>The models a request may be sent to, in attempt order, or a rejection.</summary>
public sealed record RoutePlan(IReadOnlyList<ResolvedModel> Models, RouteRejection? Rejection);

/// <summary>Outcome of candidate selection: the ordered attempt list, or a rejection when none is eligible.</summary>
public sealed record CandidateSelection(IReadOnlyList<RouteTarget> Candidates, RouteRejection? Rejection);

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
            // A rule may rewrite an unknown (e.g. legacy) name; without rules it is simply not found.
            return snapshot.Rules.Count > 0 ? new ModelResolution(null, null) : Reject(404, GatewayErrorCodes.ModelNotFound, NotFound(model));
        }

        if (key.AllowedModels.Count > 0 && !key.AllowedModels.Contains(resolved.Name, StringComparer.OrdinalIgnoreCase))
        {
            return Reject(403, GatewayErrorCodes.ModelNotAllowed, ModelNotAllowed(resolved.Name));
        }

        if (key.AllowedProviders.Count > 0
            && !resolved.Targets.Any(t => t.ModelDeployment?.ProviderAccount is { } p && RouteSelector.IsProviderAllowed(p, key.AllowedProviders)))
        {
            return Reject(403, GatewayErrorCodes.ModelNotAllowed, $"Nyckeln får inte använda någon leverantör som tillhandahåller '{resolved.Name}'.");
        }

        if (!IsCompatible(resolved.Kind, endpoint))
        {
            return Reject(400, GatewayErrorCodes.InvalidRequest, $"Modellen '{resolved.Name}' kan inte användas med denna endpoint.");
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
            return requested is null
                ? Rejected(404, GatewayErrorCodes.ModelNotFound, NotFound(requestedModel))
                : new RoutePlan([requested], null);
        }

        if (requested is null)
        {
            // The client used a name that only a rule knows. The key is entitled to what that name stands for: the
            // first model in the rule chain that exists. (A known name is checked as requested, in ResolveModel.)
            var entitled = decision.Applied.Select(a => snapshot.Resolve(a.ToModel)).FirstOrDefault(r => r is not null);
            if (entitled is null)
            {
                return Rejected(404, GatewayErrorCodes.ModelNotFound, NotFound(requestedModel));
            }

            if (key.AllowedModels.Count > 0 && !key.AllowedModels.Contains(entitled.Name, StringComparer.OrdinalIgnoreCase))
            {
                return Rejected(403, GatewayErrorCodes.ModelNotAllowed, ModelNotAllowed(entitled.Name));
            }
        }

        var models = decision.Models
            .Select(snapshot.Resolve)
            .Where(m => m is not null && IsCompatible(m.Kind, endpoint))
            .Select(m => m!)
            .ToList();
        return models.Count > 0
            ? new RoutePlan(models, null)
            : Rejected(503, GatewayErrorCodes.NoEligibleProvider, $"Routingregeln för '{requestedModel}' pekar på modeller som inte finns eller inte kan användas med denna endpoint.");
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

        var reason = restrictTo == DataResidency.OnPrem
            ? "Förfrågan innehåller personuppgifter och får bara skickas till en lokal (on-prem) modell, men ingen sådan finns för detta alias."
            : "Ingen tillgänglig leverantör kan hantera förfrågan för detta alias med nyckelns begränsningar.";
        return new CandidateSelection(candidates, new RouteRejection(503, GatewayErrorCodes.NoEligibleProvider, reason));
    }

    private static HashSet<Guid> ProviderIds(IEnumerable<ResolvedModel> models) =>
        [.. models.SelectMany(m => m.Targets).Where(t => t.ModelDeployment is not null).Select(t => t.ModelDeployment!.ProviderAccountId)];

    /// <summary>The value of the <c>endpoint</c> variable in routing conditions.</summary>
    public static string EndpointName(GatewayEndpoint endpoint) => endpoint switch
    {
        GatewayEndpoint.ChatCompletions => "chat_completions",
        GatewayEndpoint.Embeddings => "embeddings",
        GatewayEndpoint.Responses => "responses",
        GatewayEndpoint.AnthropicMessages => "anthropic_messages",
        GatewayEndpoint.AudioTranscriptions => "audio_transcriptions",
        GatewayEndpoint.AudioTranslations => "audio_translations",
        GatewayEndpoint.Realtime => "realtime",
        GatewayEndpoint.RealtimeTranslations => "realtime_translations",
        _ => endpoint.ToString().ToLowerInvariant(),
    };

    /// <summary>Which kinds of model an endpoint serves. Live transcription uses speech-to-text models on <c>/v1/realtime</c>.</summary>
    public static bool IsCompatible(ModelKind kind, GatewayEndpoint endpoint) => endpoint switch
    {
        GatewayEndpoint.Embeddings => kind == ModelKind.Embedding,
        GatewayEndpoint.AudioTranscriptions or GatewayEndpoint.AudioTranslations => kind == ModelKind.Transcription,
        GatewayEndpoint.Realtime => kind is ModelKind.Realtime or ModelKind.Transcription,
        GatewayEndpoint.RealtimeTranslations => kind == ModelKind.SpeechTranslation,
        _ => kind == ModelKind.Chat,
    };

    private static string NotFound(string model) =>
        $"Modellen '{(model.Length > 200 ? model[..200] : model)}' finns inte. Se GET /v1/models för tillgängliga modeller.";

    private static string ModelNotAllowed(string name) => $"Nyckeln får inte använda modellen '{name}'.";

    private static ModelResolution Reject(int status, string code, string message) => new(null, new RouteRejection(status, code, message));

    private static RoutePlan Rejected(int status, string code, string message) => new([], new RouteRejection(status, code, message));
}
