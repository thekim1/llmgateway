using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Providers;

/// <summary>
/// Azure AI Foundry addresses. The portal shows the project endpoint
/// (<c>https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>), and only the project API lists the
/// deployments (<c>/openai/v1/models</c> lists every model the resource could deploy), but requests go to the resource's
/// <c>…/openai/v1</c> (or, for Claude, <c>…/anthropic/v1</c>). So a provider keeps the address the admin entered, which
/// names the project, and the gateway derives the request address when it loads the catalogue.
/// </summary>
public static class FoundryEndpoint
{
    private const string ProjectsPath = "/api/projects/";
    private const string ServicesHostSuffix = ".services.ai.azure.com";
    private static readonly string[] ResourceHostSuffixes = [".openai.azure.com", ".cognitiveservices.azure.com", ServicesHostSuffix];

    /// <summary>
    /// The base URL requests are sent to. A project endpoint or a bare resource address on a Foundry resource becomes
    /// <c>…/openai/v1</c> (<c>…/anthropic/v1</c> for an Anthropic provider); any other address is used as entered.
    /// </summary>
    public static string RequestBaseUrl(ProviderType type, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (type is not (ProviderType.AzureAIFoundry or ProviderType.Anthropic) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return baseUrl;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var foundryAddress = type == ProviderType.AzureAIFoundry || IsFoundryHost(uri);
        return foundryAddress && (path.Length == 0 || IsProjectPath(path))
            ? uri.GetLeftPart(UriPartial.Authority) + (type == ProviderType.Anthropic ? "/anthropic/v1" : "/openai/v1")
            : baseUrl;
    }

    /// <summary>
    /// The project API's deployment list for the resource behind <paramref name="baseUrl"/>: the project the address names,
    /// or the default project (<c>_project</c>, which not every resource has) when it names none.
    /// </summary>
    public static Uri DeploymentsUri(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var uri = new Uri(baseUrl);
        return new UriBuilder(uri.Scheme, ServicesHost(uri.Host), uri.Port, $"{ProjectsPath}{ProjectName(baseUrl) ?? "_project"}/deployments") { Query = "api-version=v1" }.Uri;
    }

    /// <summary>
    /// Whether <paramref name="deployment"/> of an Azure AI Foundry provider is Claude, which the resource serves only through
    /// its Anthropic API (<c>…/anthropic/v1</c>), not the OpenAI v1 API the provider's other deployments use.
    /// </summary>
    public static bool IsClaude(ProviderAccount provider, ModelDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        return provider.Type == ProviderType.AzureAIFoundry && deployment.ModelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What <paramref name="deployment"/> can be called for: its provider's capabilities, or for Claude on a Foundry provider
    /// those of the Anthropic API (<see cref="AnthropicView"/>), as the gateway routes it.
    /// </summary>
    public static ProviderCapabilities Capabilities(ProviderAccount provider, ModelDeployment deployment) =>
        IsClaude(provider, deployment) ? ProviderTypes.AnthropicApi : provider.SupportedCapabilities;

    /// <summary>
    /// The resource's Anthropic API as an Anthropic provider, for the Claude deployments of a Foundry provider: the same id
    /// (so health, circuit and usage stay the provider's), name, credential, residency and switches; the address
    /// <c>https://&lt;resource&gt;.services.ai.azure.com/anthropic/v1</c>; what an Anthropic provider serves. The Anthropic API
    /// takes a key in <c>x-api-key</c>, not <c>api-key</c>, so a key moves header; a Bearer token stays one.
    /// <paramref name="foundry"/> must still have the address the admin entered.
    /// </summary>
    public static ProviderAccount AnthropicView(ProviderAccount foundry)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        var uri = new Uri(foundry.BaseUrl);
        return new ProviderAccount
        {
            Id = foundry.Id,
            Name = foundry.Name,
            DisplayName = foundry.DisplayName,
            Type = ProviderType.Anthropic,
            BaseUrl = new UriBuilder(uri.Scheme, ServicesHost(uri.Host), uri.Port, "/anthropic/v1").Uri.ToString().TrimEnd('/'),
            AuthMode = foundry.AuthMode == ProviderAuthMode.ApiKeyHeader ? ProviderAuthMode.XApiKeyHeader : foundry.AuthMode,
            EncryptedCredential = foundry.EncryptedCredential,
            Residency = foundry.Residency,
            Capabilities = ProviderTypes.AnthropicApi,
            IsEnabled = foundry.IsEnabled,
            IsDrained = foundry.IsDrained,
            TimeoutSeconds = foundry.TimeoutSeconds,
            AutoUpdatePrices = foundry.AutoUpdatePrices,
            CreatedAt = foundry.CreatedAt,
        };
    }

    /// <summary>The project named in a project endpoint, or <c>null</c>.</summary>
    public static string? ProjectName(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/') is var path && IsProjectPath(path)
            ? path[ProjectsPath.Length..].Split('/')[0]
            : null;

    /// <summary>Whether <paramref name="baseUrl"/> is on a Foundry resource (<c>…services.ai.azure.com</c>).</summary>
    public static bool IsFoundryHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && IsFoundryHost(uri);

    /// <summary>The resource's <c>…services.ai.azure.com</c> host for any of its addresses; other hosts unchanged.</summary>
    private static string ServicesHost(string host)
    {
        var suffix = ResourceHostSuffixes.FirstOrDefault(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        return suffix is null ? host : host[..^suffix.Length] + ServicesHostSuffix;
    }

    private static bool IsFoundryHost(Uri uri) => uri.Host.EndsWith(ServicesHostSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectPath(string path) =>
        path.StartsWith(ProjectsPath, StringComparison.OrdinalIgnoreCase) && path.Length > ProjectsPath.Length;
}
