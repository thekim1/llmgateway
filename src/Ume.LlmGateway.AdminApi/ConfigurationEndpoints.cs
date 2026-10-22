using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi;

public sealed record ProviderRequest(
    [property: Required, StringLength(100), RegularExpression(@"[a-z0-9][a-z0-9-]*")] string Name,
    [property: Required, StringLength(500)] string BaseUrl,
    [property: EnumDataType(typeof(ProviderType))] ProviderType Type,
    [property: EnumDataType(typeof(ProviderAuthMode))] ProviderAuthMode AuthMode,
    [property: EnumDataType(typeof(DataResidency))] DataResidency Residency,
    [property: Required] ProviderCapabilities[] Capabilities,
    [property: Range(1, 900)] int TimeoutSeconds = 120,
    bool IsEnabled = true,
    [property: StringLength(200)] string? DisplayName = null,
    [property: StringLength(2000), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Credential = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme == "http" && Residency != DataResidency.OnPrem))
        {
            yield return new ValidationResult("Enter an HTTPS address without credentials or a fragment. HTTP is only allowed for on-prem.", [nameof(BaseUrl)]);
        }
        else if (HasSecretParameter(uri.Query))
        {
            // Query parameters such as api-version are fine; credentials belong in the encrypted credential field.
            yield return new ValidationResult("The query string must not contain keys or tokens. Give them as the credential instead.", [nameof(BaseUrl)]);
        }
        if (Capabilities?.Any(c => !Enum.IsDefined(c) || c == ProviderCapabilities.None) == true)
        {
            yield return new ValidationResult("The property is invalid.", [nameof(Capabilities)]);
        }
    }

    private static bool HasSecretParameter(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Uri.UnescapeDataString(p.Split('=')[0]).ToLowerInvariant())
            .Any(name => name.Contains("key", StringComparison.Ordinal) || name.Contains("token", StringComparison.Ordinal) ||
                         name.Contains("secret", StringComparison.Ordinal) || name.Contains("password", StringComparison.Ordinal) ||
                         name.Contains("auth", StringComparison.Ordinal) || name is "sig" or "code");
}
public sealed record PriceRequest(
    [property: Range(typeof(decimal), "0", "1000000")] decimal InputPerMillionUsd,
    [property: Range(typeof(decimal), "0", "1000000")] decimal CachedInputPerMillionUsd,
    [property: Range(typeof(decimal), "0", "1000000")] decimal OutputPerMillionUsd,
    DateTimeOffset? EffectiveFrom = null,
    // Speech-to-text models billed by duration (Whisper). Omitted = 0.
    [property: Range(typeof(decimal), "0", "1000000")] decimal AudioPerMinuteUsd = 0,
    // Audio tokens of realtime and gpt-4o-transcribe models. Omitted = 0 (billed as text tokens).
    [property: Range(typeof(decimal), "0", "1000000")] decimal AudioInputPerMillionUsd = 0,
    [property: Range(typeof(decimal), "0", "1000000")] decimal AudioOutputPerMillionUsd = 0,
    // Image models billed per picture (DALL·E, FLUX). Omitted = 0 (token-billed models such as gpt-image-1 use the token prices).
    [property: Range(typeof(decimal), "0", "1000000")] decimal PerImageUsd = 0,
    // Prompt-cache writes (Anthropic: 1.25x input). Omitted = null (billed at the input price).
    [property: Range(typeof(decimal), "0", "1000000")] decimal? CacheWriteInputPerMillionUsd = null) : AdminRequest;
public sealed record ModelRequest(
    Guid ProviderId,
    [property: Required, StringLength(200)] string Name,
    [property: Required, StringLength(200)] string UpstreamModel,
    [property: EnumDataType(typeof(ModelKind))] ModelKind Kind,
    [property: EnumDataType(typeof(ParameterProfile))] ParameterProfile ParameterProfile,
    [property: Range(1, int.MaxValue)] int? ContextWindow = null,
    bool IsEnabled = true,
    PriceRequest? Price = null,
    [property: MaxLength(20)] string[]? Features = null) : AdminRequest;
/// <summary>Limits of route aliases, shared by the API and the configuration document.</summary>
public static class RouteLimits
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxTargets = 100;
    public const int MaxPriority = 1000;
    public const int MaxWeight = 1_000_000;
}
public sealed record TargetRequest(Guid ModelId, [property: Range(0, RouteLimits.MaxPriority)] int Priority, [property: Range(1, RouteLimits.MaxWeight)] int Weight) : AdminRequest;
public sealed record RouteRequest(
    [property: Required, StringLength(RouteLimits.MaxNameLength)] string Name,
    [property: EnumDataType(typeof(ModelKind))] ModelKind Kind,
    [property: Required, MinLength(1), MaxLength(RouteLimits.MaxTargets)] TargetRequest[] Targets,
    [property: StringLength(RouteLimits.MaxDescriptionLength)] string? Description = null,
    bool IsEnabled = true) : AdminRequest;
public sealed record DrainRequest(bool Drained) : AdminRequest;
public sealed record BudgetRequest(
    [property: EnumDataType(typeof(BudgetScope))] BudgetScope Scope,
    Guid ScopeId,
    [property: Range(typeof(decimal), "0", "1000000000")] decimal LimitUsd,
    [property: EnumDataType(typeof(BudgetPeriod))] BudgetPeriod Period,
    [property: Required, MaxLength(100)] int[] AlertThresholds,
    bool IsActive = true) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AlertThresholds?.Any(t => t is < 1 or > 100) == true)
        {
            yield return new ValidationResult("Alert thresholds must be 1–100 percent.", [nameof(AlertThresholds)]);
        }
    }
}
public sealed record ExchangeRequest([property: Range(typeof(decimal), "0.000001", "1000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal SekPerUnit) : AdminRequest;

public static class ConfigurationEndpoints
{
    public static void MapConfiguration(this RouteGroupBuilder api)
    {
        api = api.MapGroup("").RequireAuthorization(AdminPolicies.Read);

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
            AzureRetailPrices azurePrices, IHostEnvironment environment, CancellationToken ct) =>
            TypedResults.Ok(await ModelDiscovery.DiscoverAsync(await catalog.ProviderAsync(id, ct), clients.CreateClient(ModelDiscovery.HttpClientName), protector,
                requireHttps: environment.IsProduction(), ct, azurePrices.GetAsync)))
            .WithName("DiscoverModels").WithSummary("Lists the models the provider offers");
        providers.MapDelete("/{id:guid}", async (Guid id, ProviderCatalogService catalog, CancellationToken ct) =>
        {
            await catalog.DeleteProviderAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteProvider").WithSummary("Deletes a provider without models");

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
            .WithName("AddModelPrice").WithSummary("Adds a price; earlier prices are kept for past usage");

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

        MapBudgets(api);
        api.MapGet("/settings/exchange-rate", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.CurrentRateAsync(ct)))
            .WithName("GetExchangeRate").WithSummary("SEK per USD now (empty when none has been entered)");
        api.MapPut("/settings/exchange-rate", async (ExchangeRequest input, ProviderCatalogService catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.SetRateAsync(input.SekPerUnit, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("SetExchangeRate");
    }

    private static void MapBudgets(RouteGroupBuilder api)
    {
        var budgets = api.MapGroup("").WithTags("Budgets");
        budgets.MapGet("/budgets", async (BudgetScope? scope, Guid? scopeId, BudgetAdminService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(scope, scopeId, ct)))
            .WithName("ListBudgets").WithSummary("Budgets the user may see, with spend in the current period");
        budgets.MapPost("/budgets", async (BudgetRequest input, BudgetAdminService service, CancellationToken ct) =>
        {
            var budget = await service.CreateAsync(input, ct);
            return TypedResults.Created($"/api/budgets/{budget.Id}", budget);
        }).RequireAuthorization(AdminPolicies.Manage).WithName("CreateBudget");
        budgets.MapPut("/budgets/{id:guid}", async (Guid id, BudgetRequest input, BudgetAdminService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
            .RequireAuthorization(AdminPolicies.Manage).WithName("UpdateBudget");
        budgets.MapDelete("/budgets/{id:guid}", async (Guid id, BudgetAdminService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Manage).WithName("DeleteBudget");
        budgets.MapGet("/alerts", async (bool? acknowledged, BudgetAdminService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AlertsAsync(acknowledged, ct)))
            .WithName("ListAlerts").WithSummary("Budget alerts the user may see, newest first");
        budgets.MapPost("/alerts/{id:guid}/acknowledge", async (Guid id, BudgetAdminService service, CancellationToken ct) =>
        {
            await service.AcknowledgeAsync(id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Manage).WithName("AcknowledgeAlert");
    }
}
