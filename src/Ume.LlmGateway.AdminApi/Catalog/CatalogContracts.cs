using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Catalog.Pricing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.AdminApi.Catalog;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record ProviderRequest(
    [property: Required, StringLength(100), RegularExpression(@"[a-z0-9][a-z0-9-]*")] string Name,
    [property: Required, StringLength(500)] string BaseUrl,
    [property: EnumDataType(typeof(ProviderType))] ProviderType Type,
    [property: EnumDataType(typeof(ProviderAuthMode))] ProviderAuthMode AuthMode,
    [property: EnumDataType(typeof(DataResidency))] DataResidency Residency,
    // The endpoints an OpenAI-compatible server offers; other types serve what their API offers (ProviderTypes), so it is
    // ignored for them. Omitted = unchanged (a new OpenAI-compatible server: chat, streaming and embeddings).
    [property: MaxLength(16)] ProviderCapabilities[]? Capabilities,
    [property: Range(1, 900)] int TimeoutSeconds = 120,
    bool IsEnabled = true,
    [property: StringLength(200)] string? DisplayName = null,
    [property: StringLength(2000), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Credential = null,
    // The provider's models follow the models.dev list price (each model can opt out). Omitted = unchanged (a new provider: on).
    bool? AutoUpdatePrices = null) : AdminRequest, IValidatableObject
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
        if (Type == ProviderType.OpenAICompatible && Capabilities is { Length: 0 })
        {
            yield return new ValidationResult("Choose at least one endpoint the server offers.", [nameof(Capabilities)]);
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
    [property: MaxLength(20)] string[]? Features = null,
    // The price follows the models.dev list price when the model is listed. Omitted = unchanged (a new model: on).
    bool? AutoUpdatePrice = null,
    // Omitted = unchanged; empty removes it.
    [property: StringLength(1000)] string? Description = null,
    // The model behind an Azure deployment (gpt-4.1 behind prod-chat). Omitted = unchanged; empty removes it.
    [property: StringLength(200)] string? BaseModel = null) : AdminRequest;

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

public sealed record ProviderDto(
    Guid Id, string Name, string? DisplayName, ProviderType Type, string BaseUrl, ProviderAuthMode AuthMode, bool HasCredential,
    DataResidency Residency, ProviderCapabilities[] Capabilities, bool IsEnabled, bool IsDrained, int TimeoutSeconds, DateTimeOffset CreatedAt,
    int DeploymentCount, bool AutoUpdatePrices, string? PriceList)
{
    /// <summary>
    /// The provider; <c>autoUpdatePrices</c>: its models follow the models.dev list price unless a model opts out. <c>priceList</c>:
    /// the models.dev provider its models are looked up in, null when it has none (then every price is set by an admin).
    /// </summary>
    public static ProviderDto Of(ProviderAccount p) => new(
        p.Id, p.Name, p.DisplayName, p.Type, p.BaseUrl, p.AuthMode, !string.IsNullOrEmpty(p.EncryptedCredential), p.Residency,
        ProviderCatalogService.Capabilities(p.SupportedCapabilities), p.IsEnabled, p.IsDrained, p.TimeoutSeconds, p.CreatedAt, p.Deployments.Count,
        p.AutoUpdatePrices, ModelList.ProviderId(p));
}

public sealed record PriceDto(
    decimal InputPerMillionUsd, decimal CachedInputPerMillionUsd, decimal OutputPerMillionUsd, decimal AudioPerMinuteUsd,
    decimal AudioInputPerMillionUsd, decimal AudioOutputPerMillionUsd, DateTimeOffset EffectiveFrom, decimal PerImageUsd = 0,
    decimal? CacheWriteInputPerMillionUsd = null, string? Source = null)
{
    /// <summary>The price; <c>source</c> is where it came from (e.g. <c>models.dev anthropic/claude-opus-4-5</c>), null when an admin entered it.</summary>
    public static PriceDto Of(ModelPrice p) => new(
        p.InputPerMillionUsd, p.CachedInputPerMillionUsd, p.OutputPerMillionUsd, p.AudioPerMinuteUsd, p.AudioInputPerMillionUsd,
        p.AudioOutputPerMillionUsd, p.EffectiveFrom, p.PerImageUsd, p.CacheWriteInputPerMillionUsd, p.Source);
}

public sealed record ModelDto(
    Guid Id, Guid ProviderId, string? ProviderName, DataResidency? Residency, string Name, string UpstreamModel, ModelKind Kind,
    ParameterProfile ParameterProfile, int? ContextWindow, bool IsEnabled, string[] Features, PriceDto? CurrentPrice, string PublicName,
    string? Description, bool AutoUpdatePrice, PriceMode PriceMode, string? PriceListKey, string? BaseModel)
{
    /// <summary>
    /// The model; <c>publicName</c> is what clients call it: its name, or provider/name when another provider has the same name.
    /// <c>priceMode</c>: <c>List</c> follows the models.dev price of <c>priceListKey</c> (admins cannot add prices),
    /// <c>Unlisted</c> is not in the list (admins set the price until it is), <c>Manual</c> has automatic prices turned off for
    /// the model, its provider or the gateway, or is an Azure deployment (priced by Azure's list; the models.dev price only
    /// fills a missing one). <c>priceListKey</c> is the model's models.dev entry, null when it has none. <c>baseModel</c>: the
    /// model behind an Azure deployment.
    /// </summary>
    public static ModelDto Of(ModelDeployment m, DateTimeOffset now, string publicName, ModelList list, bool updatesOn) => new(
        m.Id, m.ProviderAccountId, m.ProviderAccount?.Name, m.ProviderAccount?.Residency, m.Name, m.UpstreamModel, m.Kind,
        m.ParameterProfile, m.ContextWindow, m.IsEnabled, m.Features, m.PriceAt(now) is { } p ? PriceDto.Of(p) : null, publicName,
        m.Description, m.AutoUpdatePrice, list.Mode(m, updatesOn),
        list.Find(m)?.Key, m.BaseModel);
}

public sealed record RouteDto(Guid Id, string Name, string? Description, ModelKind Kind, bool IsEnabled, RouteTargetDto[] Targets)
{
    public static RouteDto Of(RouteAlias r) => new(
        r.Id, r.Name, r.Description, r.Kind, r.IsEnabled,
        [.. r.Targets.OrderBy(t => t.Priority).Select(t => new RouteTargetDto(
            t.ModelDeploymentId, t.ModelDeployment?.Name, t.ModelDeployment?.ProviderAccount?.Name, t.ModelDeployment?.ProviderAccount?.Residency,
            t.Priority, t.Weight))]);
}

public sealed record RouteTargetDto(Guid ModelId, string? ModelName, string? ProviderName, DataResidency? Residency, int Priority, int Weight);

public sealed record CatalogDto(string GatewayBaseUrl, IReadOnlyList<CatalogModelDto> Models, IReadOnlyList<CatalogRouteDto> Routes);

public sealed record CatalogModelDto(
    string Name, ModelKind Kind, DataResidency Residency, ProviderCapabilities[] Capabilities, decimal? InputUsdPerMillion, decimal? OutputUsdPerMillion,
    string? Description = null, string[]? Features = null);

public sealed record CatalogRouteDto(
    string Name, string? Description, ModelKind Kind, DataResidency[] Residencies, ProviderCapabilities[] Capabilities,
    decimal? InputUsdPerMillion, decimal? OutputUsdPerMillion);
