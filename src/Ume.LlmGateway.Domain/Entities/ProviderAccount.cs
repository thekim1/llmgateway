using Ume.LlmGateway.Domain.Providers;

namespace Ume.LlmGateway.Domain.Entities;

public sealed class ProviderAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Unique slug, e.g. <c>ollama-local</c>, <c>azure-swc</c>.</summary>
    public required string Name { get; set; }

    public string? DisplayName { get; set; }
    public ProviderType Type { get; set; }

    /// <summary>Base URL including version segment, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; set; }

    public ProviderAuthMode AuthMode { get; set; }

    /// <summary>Credential encrypted with ASP.NET Data Protection. Never returned by any API.</summary>
    public string? EncryptedCredential { get; set; }

    public DataResidency Residency { get; set; }
    /// <summary>
    /// The endpoints an OpenAI-compatible server offers, as the admin chose them. Other types serve what their API offers
    /// (<see cref="SupportedCapabilities"/>), whatever is stored here.
    /// </summary>
    public ProviderCapabilities Capabilities { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Operator flag: take provider out of rotation without deleting configuration.</summary>
    public bool IsDrained { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>The provider's models follow the models.dev list price (each model can opt out). Off, admins set every price.</summary>
    public bool AutoUpdatePrices { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public List<ModelDeployment> Deployments { get; set; } = [];

    public bool IsAvailable => IsEnabled && !IsDrained;

    /// <summary>What the provider serves: fixed by its type (<see cref="ProviderTypes"/>), or for an OpenAI-compatible server <see cref="Capabilities"/>.</summary>
    public ProviderCapabilities SupportedCapabilities => ProviderTypes.Capabilities(Type, Capabilities);

    public bool Supports(GatewayEndpoint endpoint) => GatewayEndpoints.Info(endpoint).IsSupportedBy(SupportedCapabilities);
}
