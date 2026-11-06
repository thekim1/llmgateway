namespace Ume.LlmGateway.Domain.Entities;

/// <summary>A concrete model offered by a provider account.</summary>
public sealed class ModelDeployment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProviderAccountId { get; set; }
    public ProviderAccount? ProviderAccount { get; set; }

    /// <summary>Gateway-visible name, unique, e.g. <c>azure-swc/gpt-4o-mini</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Model id or Azure deployment name sent upstream.</summary>
    public required string UpstreamModel { get; set; }

    /// <summary>
    /// The model behind an Azure deployment (<c>gpt-4.1</c> behind deployment <c>prod-chat</c>), set by discovery; null when
    /// <see cref="UpstreamModel"/> is the model. Finds the model in the models.dev list, and a Claude base model on an Azure AI
    /// Foundry provider is served through the resource's Anthropic API.
    /// </summary>
    public string? BaseModel { get; set; }

    /// <summary>The model's own name: <see cref="BaseModel"/>, else <see cref="UpstreamModel"/>.</summary>
    public string ModelName => BaseModel ?? UpstreamModel;

    /// <summary>
    /// Where this deployment processes data, when it differs from its provider's <see cref="ProviderAccount.Residency"/>; null =
    /// the provider's. One Foundry resource can hold global, data zone and regional deployments, which do not all process
    /// data in the same place. Routing, a key's residencies, the PII on-prem restriction, the residency ceiling and
    /// <c>/v1/models</c> use <see cref="EffectiveResidency"/>.
    /// </summary>
    public DataResidency? Residency { get; set; }

    /// <summary>
    /// The publisher of the model behind an Azure AI Foundry deployment (<c>Cohere</c>, <c>OpenAI</c>, <c>Meta</c>), set by
    /// discovery from the deployment's <c>modelPublisher</c>. A Foundry rerank deployment is called at
    /// <c>…/providers/&lt;publisher&gt;/v2</c> (<see cref="Providers.FoundryEndpoint.RerankBaseUrl"/>).
    /// </summary>
    public string? Publisher { get; set; }

    /// <summary>
    /// The base URL requests for a rerank deployment of an Azure AI Foundry provider go to (the gateway appends
    /// <c>/rerank</c>), when the address derived from <see cref="Publisher"/> is not the right one; null = derived. Only an
    /// address on the provider's own resource is used, since the provider's credential goes with it.
    /// </summary>
    public string? RequestBaseUrl { get; set; }

    /// <summary>How a Foundry rerank deployment sends the provider's credential, when not as the provider does; null = the provider's.</summary>
    public ProviderAuthMode? RequestAuthMode { get; set; }

    /// <summary>Where the deployment processes data: its own <see cref="Residency"/>, else its provider's.</summary>
    public DataResidency EffectiveResidency(ProviderAccount provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Residency ?? provider.Residency;
    }

    public ModelKind Kind { get; set; }
    public ParameterProfile ParameterProfile { get; set; }
    public int? ContextWindow { get; set; }
    /// <summary>What the model can do, e.g. tools, vision, thinking, embedding, image generation. Free-form tags.</summary>
    public string[] Features { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
    /// <summary>
    /// The price follows the models.dev list price, updated daily, when the model is in the list and its provider also has
    /// <see cref="ProviderAccount.AutoUpdatePrices"/> on. Off, or for a model not in the list, an admin sets the price.
    /// </summary>
    public bool AutoUpdatePrice { get; set; } = true;

    /// <summary>What the model is good for, shown in the catalogue. Filled from models.dev when empty.</summary>
    public string? Description { get; set; }
    public List<ModelPrice> Prices { get; set; } = [];

    public ModelPrice? PriceAt(DateTimeOffset when) =>
        Prices.Where(p => p.EffectiveFrom <= when).MaxBy(p => p.EffectiveFrom);
}
