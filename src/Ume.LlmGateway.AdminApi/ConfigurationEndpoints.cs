using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
            yield return new ValidationResult("Ange en HTTPS-adress utan inloggningsuppgifter eller fragment. HTTP är endast tillåtet för on-prem.", [nameof(BaseUrl)]);
        }
        else if (HasSecretParameter(uri.Query))
        {
            // Query parameters such as api-version are fine; credentials belong in the encrypted credential field.
            yield return new ValidationResult("Frågesträngen får inte innehålla nycklar eller token. Ange dem som autentiseringsuppgift i stället.", [nameof(BaseUrl)]);
        }
        if (Capabilities?.Any(c => !Enum.IsDefined(c) || c == ProviderCapabilities.None) == true)
        {
            yield return new ValidationResult("Egenskapen är ogiltig.", [nameof(Capabilities)]);
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
    [property: Range(typeof(decimal), "0", "1000000")] decimal AudioOutputPerMillionUsd = 0) : AdminRequest;
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
    [property: Range(typeof(decimal), "0", "1000000000")] decimal LimitSek,
    [property: EnumDataType(typeof(BudgetPeriod))] BudgetPeriod Period,
    [property: Required, MaxLength(100)] int[] AlertThresholds,
    bool IsActive = true) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AlertThresholds?.Any(t => t is < 1 or > 100) == true)
        {
            yield return new ValidationResult("Larmgränser måste vara 1–100 procent.", [nameof(AlertThresholds)]);
        }
    }
}
public sealed record ExchangeRequest([property: Range(typeof(decimal), "0.000001", "1000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal SekPerUnit) : AdminRequest;

public static class ConfigurationEndpoints
{
    public static void MapConfiguration(this RouteGroupBuilder api)
    {
        api = api.MapGroup("").RequireAuthorization("read");

        var providers = api.MapGroup("/providers").RequireAuthorization("admin").WithTags("Providers");
        providers.MapGet("", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ProvidersAsync(ct)))
            .WithName("ListProviders").WithSummary("Providers with their deployment count");
        providers.MapPost("", async (ProviderRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var provider = await catalog.CreateProviderAsync(user, input, ct);
            return TypedResults.Created($"/api/providers/{provider.Id}", provider);
        }).WithName("CreateProvider").WithSummary("Adds a provider; the credential is stored encrypted and never returned");
        providers.MapPut("/{id:guid}", async (Guid id, ProviderRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await catalog.UpdateProviderAsync(user, id, input, ct)))
            .WithName("UpdateProvider").WithSummary("Changes a provider; omit the credential to keep it, send an empty one to remove it");
        providers.MapPost("/{id:guid}/drain", async (Guid id, DrainRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await catalog.DrainAsync(user, id, input.Drained, ct)))
            .WithName("DrainProvider").WithSummary("Stops (or resumes) routing new requests to a provider");
        providers.MapPost("/{id:guid}/discover-models", async (Guid id, ProviderCatalogService catalog, CredentialProtector protector, IHttpClientFactory clients, CancellationToken ct) =>
            TypedResults.Ok(await ModelDiscovery.DiscoverAsync(await catalog.ProviderAsync(id, ct), clients.CreateClient("provider-discovery"), protector, ct)))
            .WithName("DiscoverModels").WithSummary("Lists the models the provider offers");
        providers.MapDelete("/{id:guid}", async (Guid id, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await catalog.DeleteProviderAsync(user, id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteProvider").WithSummary("Deletes a provider without models");

        var models = api.MapGroup("/models").RequireAuthorization("admin").WithTags("Models");
        models.MapGet("", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ModelsAsync(ct)))
            .WithName("ListModels").WithSummary("Model deployments with their current price");
        models.MapPost("", async (ModelRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var model = await catalog.CreateModelAsync(user, input, ct);
            return TypedResults.Created($"/api/models/{model.Id}", model);
        }).WithName("CreateModel");
        models.MapPut("/{id:guid}", async (Guid id, ModelRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await catalog.UpdateModelAsync(user, id, input, ct)))
            .WithName("UpdateModel");
        models.MapDelete("/{id:guid}", async (Guid id, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await catalog.DeleteModelAsync(user, id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteModel").WithSummary("Deletes a model that no route or routing rule uses");
        models.MapGet("/{id:guid}/prices", async (Guid id, ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.PricesAsync(id, ct)))
            .WithName("ListModelPrices").WithSummary("Price history, newest first");
        models.MapPost("/{id:guid}/prices", async (Guid id, PriceRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Created($"/api/models/{id}/prices", await catalog.AddPriceAsync(user, id, input, ct)))
            .WithName("AddModelPrice").WithSummary("Adds a price; earlier prices are kept for past usage");

        var routes = api.MapGroup("/routes").RequireAuthorization("admin").WithTags("Routes");
        routes.MapGet("", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.RoutesAsync(ct)))
            .WithName("ListRoutes");
        routes.MapPost("", async (RouteRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var route = await catalog.CreateRouteAsync(user, input, ct);
            return TypedResults.Created($"/api/routes/{route.Id}", route);
        }).WithName("CreateRoute");
        routes.MapPut("/{id:guid}", async (Guid id, RouteRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await catalog.UpdateRouteAsync(user, id, input, ct)))
            .WithName("UpdateRoute");
        routes.MapDelete("/{id:guid}", async (Guid id, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await catalog.DeleteRouteAsync(user, id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteRoute");

        MapBudgets(api);
        api.MapGet("/settings/exchange-rate", async (ProviderCatalogService catalog, CancellationToken ct) => TypedResults.Ok(await catalog.CurrentRateAsync(ct)))
            .WithName("GetExchangeRate").WithSummary("SEK per USD now (empty when none has been entered)");
        api.MapPut("/settings/exchange-rate", async (ExchangeRequest input, ProviderCatalogService catalog, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await catalog.SetRateAsync(user, input.SekPerUnit, ct)))
            .RequireAuthorization("admin").WithName("SetExchangeRate");
    }

    private static void MapBudgets(RouteGroupBuilder api)
    {
        var budgets = api.MapGroup("").WithTags("Budgets");
        budgets.MapGet("/budgets", async (BudgetScope? scope, Guid? scopeId, BudgetAdminService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(user, scope, scopeId, ct)))
            .WithName("ListBudgets").WithSummary("Budgets the user may see, with spend in the current period");
        budgets.MapPost("/budgets", async (BudgetRequest input, BudgetAdminService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var budget = await service.CreateAsync(user, input, ct);
            return TypedResults.Created($"/api/budgets/{budget.Id}", budget);
        }).RequireAuthorization("manage").WithName("CreateBudget");
        budgets.MapPut("/budgets/{id:guid}", async (Guid id, BudgetRequest input, BudgetAdminService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(user, id, input, ct)))
            .RequireAuthorization("manage").WithName("UpdateBudget");
        budgets.MapDelete("/budgets/{id:guid}", async (Guid id, BudgetAdminService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await service.DeleteAsync(user, id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization("manage").WithName("DeleteBudget");
        budgets.MapGet("/alerts", async (bool? acknowledged, BudgetAdminService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.AlertsAsync(user, acknowledged, ct)))
            .WithName("ListAlerts").WithSummary("Budget alerts the user may see, newest first");
        budgets.MapPost("/alerts/{id:guid}/acknowledge", async (Guid id, BudgetAdminService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await service.AcknowledgeAsync(user, id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization("manage").WithName("AcknowledgeAlert");
    }
}
