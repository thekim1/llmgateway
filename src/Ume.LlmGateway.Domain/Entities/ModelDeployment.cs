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
