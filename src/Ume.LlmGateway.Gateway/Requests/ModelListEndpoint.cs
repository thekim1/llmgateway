using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Providers;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Gateway.Routing;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// <c>GET /v1/models</c>: what the key can use, decided by the same eligibility rule as routing, within the request's
/// residency ceiling when it sends one (so a client can ask what it may use at that level).
/// </summary>
public sealed class ModelListEndpoint(RequestStages stages, GatewayCatalog catalog, TimeProvider time)
{
    public async Task ListModelsAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var key = await stages.AuthenticateAsync(http, GatewayEndpoint.Models, time.GetUtcNow(), ct);
        if (key is null)
        {
            return;
        }

        if (RequestStages.ReadMaxResidency(http.Request, key, out var ceiling) is { } ceilingError)
        {
            await stages.WriteErrorAsync(http, GatewayEndpoint.Models, ceilingError);
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        var now = time.GetUtcNow();
        // The endpoint is not used: a model is listed when some endpoint of its kind could route to it.
        var constraints = new RoutingConstraints(GatewayEndpoint.Models, RouteSelector.EffectiveResidencies(key.AllowedResidencies, null, ceiling), null, key.AllowedProviders);

        var data = new JsonArray();
        foreach (var route in snapshot.Routes.Values.Where(r => r.IsEnabled && key.IsModelAllowed(r.Name)).OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var available = route.Targets.Where(t => RouteSelector.IsEligibleForAnyEndpoint(t.ModelDeployment, route.Kind, constraints)).ToList();
            if (available.Count == 0)
            {
                continue;
            }

            data.Add(new JsonObject
            {
                ["id"] = route.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "ume-llm-gateway",
                ["ume"] = AliasFacts(route, available, now),
            });
        }

        // Public names (provider/name when several providers have the name); a deployment whose name a route also has
        // is listed under provider/name, since the plain name means the route.
        foreach (var (id, deployment) in snapshot.Deployments
                     .Select(d => (Id: snapshot.Routes.ContainsKey(d.Key) ? PublicModelNames.Qualified(d.Value.ProviderAccount!.Name, d.Value.Name) : d.Key, Deployment: d.Value))
                     .Where(d => snapshot.Resolve(d.Id) is { } resolved && resolved.IsAllowedFor(key) && RouteSelector.IsEligibleForAnyEndpoint(d.Deployment, d.Deployment.Kind, constraints))
                     .OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            data.Add(new JsonObject
            {
                ["id"] = id,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = deployment.ProviderAccount!.Name,
                ["ume"] = ModelFacts(deployment, now),
            });
        }

        await CacheableJson.WriteAsync(http, new JsonObject { ["object"] = "list", ["data"] = data }, ct);
    }

    /// <summary>
    /// An alias's facts from the targets the key may use: every residency, and the limits and features all of them have,
    /// so a client that keeps within them is served by whichever target answers. The price is the first target's
    /// (lowest priority number), the one normally used; the cost on the answer is the one charged.
    /// </summary>
    private static JsonObject AliasFacts(RouteAlias route, IReadOnlyList<RouteTarget> available, DateTimeOffset now)
    {
        var deployments = available.Select(t => t.ModelDeployment!).ToList();
        var first = available.OrderBy(t => t.Priority).ThenByDescending(t => t.Weight).First().ModelDeployment!;
        var features = deployments.Skip(1).Aggregate((IEnumerable<string>)first.Features, (common, d) => common.Intersect(d.Features, StringComparer.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["type"] = "alias",
            ["kind"] = route.Kind.ToString(),
            ["description"] = route.Description,
            ["residencies"] = new JsonArray([.. deployments.Select(d => d.EffectiveResidency(d.ProviderAccount!).ToString()).Distinct().Select(r => (JsonNode?)JsonValue.Create(r))]),
            ["context_window"] = Smallest(deployments.Select(d => d.ContextWindow)),
            ["max_output_tokens"] = Smallest(deployments.Select(d => d.MaxOutputTokens)),
            ["features"] = Strings(features),
            ["pricing"] = Pricing(first, now),
        };
    }

    private static JsonObject ModelFacts(ModelDeployment deployment, DateTimeOffset now) => new()
    {
        ["type"] = "model",
        ["kind"] = deployment.Kind.ToString(),
        ["residency"] = deployment.EffectiveResidency(deployment.ProviderAccount!).ToString(),
        ["context_window"] = deployment.ContextWindow,
        ["max_output_tokens"] = deployment.MaxOutputTokens,
        ["features"] = Strings(deployment.Features),
        ["pricing"] = Pricing(deployment, now),
    };

    /// <summary>The smallest known value; null when a target does not know it (the alias's limit is then unknown too).</summary>
    private static int? Smallest(IEnumerable<int?> values)
    {
        int? smallest = null;
        foreach (var value in values)
        {
            if (value is not { } known)
            {
                return null;
            }

            smallest = smallest is { } s ? Math.Min(s, known) : known;
        }

        return smallest;
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new([.. values.Order(StringComparer.Ordinal).Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>The price in effect now, in USD per million tokens; null when the model has none.</summary>
    private static JsonObject? Pricing(ModelDeployment deployment, DateTimeOffset now) => deployment.PriceAt(now) is { } price
        ? new JsonObject
        {
            ["input_per_million_usd"] = price.InputPerMillionUsd,
            ["cached_input_per_million_usd"] = price.CachedInputPerMillionUsd,
            ["output_per_million_usd"] = price.OutputPerMillionUsd,
        }
        : null;
}
