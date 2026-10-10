using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary><c>GET /v1/models</c>: what the key can use, decided by the same eligibility rule as routing.</summary>
public sealed partial class GatewayRequestHandler
{
    public async Task ListModelsAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var key = await AuthenticateAsync(http, GatewayEndpoint.Models, time.GetUtcNow(), ct);
        if (key is null)
        {
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        // The endpoint is not used: a model is listed when some endpoint of its kind could route to it.
        var constraints = new RoutingConstraints(GatewayEndpoint.Models, RouteSelector.EffectiveResidencies(key.AllowedResidencies, null), null, key.AllowedProviders);

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
                ["ume"] = new JsonObject
                {
                    ["type"] = "alias",
                    ["kind"] = route.Kind.ToString(),
                    ["description"] = route.Description,
                    ["residencies"] = new JsonArray([.. available.Select(t => (JsonNode?)JsonValue.Create(t.ModelDeployment!.ProviderAccount!.Residency.ToString())).Distinct()]),
                },
            });
        }

        foreach (var deployment in snapshot.Deployments.Values
                     .Where(d => key.IsModelAllowed(d.Name) && RouteSelector.IsEligibleForAnyEndpoint(d, d.Kind, constraints))
                     .OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            data.Add(new JsonObject
            {
                ["id"] = deployment.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = deployment.ProviderAccount!.Name,
                ["ume"] = new JsonObject
                {
                    ["type"] = "model",
                    ["kind"] = deployment.Kind.ToString(),
                    ["residency"] = deployment.ProviderAccount.Residency.ToString(),
                },
            });
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString(), ct);
    }
}
