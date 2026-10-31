using Ume.LlmGateway.AdminApi.Catalog.Pricing;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Catalog;

/// <summary>Providers, their model deployments and prices, and route aliases. Gateway admins only.</summary>
public static class CatalogEndpoints
{
    public static void MapCatalog(this RouteGroupBuilder api)
    {
        var providers = api.MapGroup("/providers").RequireAuthorization(AdminPolicies.Admin).WithTags("Providers");
        providers.MapGet("", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ProvidersAsync(ct)))
            .WithName("ListProviders").WithSummary("Providers with their deployment count");
        providers.MapPost("", async (ProviderRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            var provider = await catalog.CreateProviderAsync(input, ct);
            return TypedResults.Created($"/api/providers/{provider.Id}", provider);
        }).WithName("CreateProvider").WithSummary("Adds a provider; the credential is stored encrypted and never returned");
        providers.MapPut("/{id:guid}", async (Guid id, ProviderRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.UpdateProviderAsync(id, input, ct)))
            .WithName("UpdateProvider").WithSummary("Changes a provider; omit the credential to keep it, send an empty one to remove it");
        providers.MapPost("/{id:guid}/drain", async (Guid id, DrainRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.DrainAsync(id, input.Drained, ct)))
            .WithName("DrainProvider").WithSummary("Stops (or resumes) routing new requests to a provider");
        providers.MapPost("/{id:guid}/discover-models", async (Guid id, ProviderCatalogService catalog, CredentialProtector protector, IHttpClientFactory clients,
            AzureRetailPrices azurePrices, ModelListService modelList, IHostEnvironment environment, CancellationToken ct) =>
            TypedResults.Ok(await ModelDiscovery.DiscoverAsync(await catalog.ProviderAsync(id, ct), clients.CreateClient(ModelDiscovery.HttpClientName), protector,
                requireHttps: environment.IsProduction(), ct, azurePrices.GetAsync, c => modelList.GetAsync(c))))
            .WithName("DiscoverModels").WithSummary("Lists the models the provider offers");
        providers.MapDelete("/{id:guid}", async (Guid id, bool? deleteModels, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            await catalog.DeleteProviderAsync(id, deleteModels ?? false, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteProvider").WithSummary("Deletes a provider; one with models only with deleteModels=true, and only when no route or routing rule uses them");

        var models = api.MapGroup("/models").RequireAuthorization(AdminPolicies.Admin).WithTags("Models");
        models.MapGet("", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ModelsAsync(ct)))
            .WithName("ListModels").WithSummary("Model deployments with their current price");
        models.MapPost("", async (ModelRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            var model = await catalog.CreateModelAsync(input, ct);
            return TypedResults.Created($"/api/models/{model.Id}", model);
        }).WithName("CreateModel");
        models.MapPut("/{id:guid}", async (Guid id, ModelRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.UpdateModelAsync(id, input, ct)))
            .WithName("UpdateModel");
        models.MapDelete("/{id:guid}", async (Guid id, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            await catalog.DeleteModelAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteModel").WithSummary("Deletes a model that no route or routing rule uses");
        models.MapGet("/{id:guid}/prices", async (Guid id, ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.PricesAsync(id, ct)))
            .WithName("ListModelPrices").WithSummary("Price history, newest first");
        models.MapPost("/{id:guid}/prices", async (Guid id, PriceRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
            TypedResults.Created($"/api/models/{id}/prices", await catalog.AddPriceAsync(id, input, ct)))
            .WithName("AddModelPrice").WithSummary("Adds a price (409 while the model follows the models.dev list price); earlier prices are kept for past usage");

        var routes = api.MapGroup("/routes").RequireAuthorization(AdminPolicies.Admin).WithTags("Routes");
        routes.MapGet("", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.RoutesAsync(ct)))
            .WithName("ListRoutes");
        routes.MapPost("", async (RouteRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            var route = await catalog.CreateRouteAsync(input, ct);
            return TypedResults.Created($"/api/routes/{route.Id}", route);
        }).WithName("CreateRoute");
        routes.MapPut("/{id:guid}", async (Guid id, RouteRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.UpdateRouteAsync(id, input, ct)))
            .WithName("UpdateRoute");
        routes.MapDelete("/{id:guid}", async (Guid id, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            await catalog.DeleteRouteAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteRoute");
    }
}
