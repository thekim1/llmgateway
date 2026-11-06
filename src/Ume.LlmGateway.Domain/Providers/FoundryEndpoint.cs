using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Providers;

/// <summary>
/// Azure AI Foundry addresses. The portal shows the project endpoint
/// (<c>https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>), and only the project API lists the
/// deployments (<c>/openai/v1/models</c> lists every model the resource could deploy), but requests go to the resource's
/// <c>…/openai/v1</c> (for Claude <c>…/anthropic/v1</c>, for a reranker its publisher's <c>…/providers/&lt;publisher&gt;/v2</c>).
/// So a provider keeps the address the admin entered, which names the project, and the gateway derives the request address
/// when it loads the catalogue.
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
    /// Whether <paramref name="deployment"/> of an Azure AI Foundry provider is a reranker (kind <see cref="ModelKind.Rerank"/>),
    /// which the resource serves through its publisher's own API (<c>…/providers/&lt;publisher&gt;/v2/rerank</c> for Cohere),
    /// not the OpenAI v1 API.
    /// </summary>
    public static bool IsRerank(ProviderAccount provider, ModelDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        return provider.Type == ProviderType.AzureAIFoundry && deployment.Kind == ModelKind.Rerank;
    }

    /// <summary>
    /// What <paramref name="deployment"/> can be called for: its provider's capabilities; for Claude on a Foundry provider
    /// those of the Anthropic API (<see cref="AnthropicView"/>); for a Foundry reranker <c>Rerank</c> when it has a request
    /// address (<see cref="RerankBaseUrl"/>), else nothing.
    /// </summary>
    public static ProviderCapabilities Capabilities(ProviderAccount provider, ModelDeployment deployment) =>
        IsRerank(provider, deployment) ? (RerankBaseUrl(provider, deployment) is null ? ProviderCapabilities.None : ProviderCapabilities.Rerank)
        : IsClaude(provider, deployment) ? ProviderTypes.AnthropicApi
        : provider.SupportedCapabilities;

    /// <summary>
    /// The resource's Anthropic API as an Anthropic provider, for the Claude deployments of a Foundry provider: the same id
    /// (so health, circuit and usage stay the provider's), name, credential, residency and switches; the address
    /// <c>https://&lt;resource&gt;.services.ai.azure.com/anthropic/v1</c>; what an Anthropic provider serves. The Anthropic API
    /// takes a key in <c>x-api-key</c>, not <c>api-key</c>, so a key moves header; a Bearer token stays one.
    /// </summary>
    public static ProviderAccount AnthropicView(ProviderAccount foundry)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        var uri = new Uri(foundry.BaseUrl);
        return foundry.View(
            ProviderType.Anthropic,
            new UriBuilder(uri.Scheme, ServicesHost(uri.Host), uri.Port, "/anthropic/v1").Uri.ToString().TrimEnd('/'),
            foundry.AuthMode == ProviderAuthMode.ApiKeyHeader ? ProviderAuthMode.XApiKeyHeader : foundry.AuthMode,
            ProviderTypes.AnthropicApi,
            foundry.Residency);
    }

    /// <summary>
    /// The base URL a Foundry rerank deployment is called at (the gateway appends <c>/rerank</c>): the deployment's
    /// <see cref="ModelDeployment.RequestBaseUrl"/> when it is on the provider's resource (<see cref="IsOnResource"/>; another
    /// host would receive the provider's credential, so it is not used), else
    /// <c>https://&lt;resource&gt;.services.ai.azure.com/providers/&lt;publisher&gt;/v2</c> from its
    /// <see cref="ModelDeployment.Publisher"/> (lower case). Null when neither gives an address: the deployment is then not routed.
    /// Nothing about a publisher is in code, so a new publisher, or a changed route, is configuration.
    /// </summary>
    public static string? RerankBaseUrl(ProviderAccount foundry, ModelDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        ArgumentNullException.ThrowIfNull(deployment);
        if (!string.IsNullOrWhiteSpace(deployment.RequestBaseUrl))
        {
            var custom = deployment.RequestBaseUrl.Trim();
            return IsOnResource(foundry.BaseUrl, custom) ? custom.TrimEnd('/') : null;
        }

        if (string.IsNullOrWhiteSpace(deployment.Publisher) || !Uri.TryCreate(foundry.BaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var publisher = Uri.EscapeDataString(deployment.Publisher.Trim().ToLowerInvariant());
        return new UriBuilder(uri.Scheme, ServicesHost(uri.Host), uri.Port, $"/providers/{publisher}/v2").Uri.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>
    /// The provider as a Foundry rerank deployment is called: the same id, name, credential, residency and switches (so
    /// health, circuit and usage stay the provider's), the address <see cref="RerankBaseUrl"/>, the provider's authentication
    /// unless the deployment sets its own (<see cref="ModelDeployment.RequestAuthMode"/>), and only <c>Rerank</c>. Requests
    /// and answers pass through unchanged (Cohere's format), as for an OpenAI-compatible reranker. Null when the deployment
    /// has no request address.
    /// </summary>
    public static ProviderAccount? RerankView(ProviderAccount foundry, ModelDeployment deployment) =>
        RerankBaseUrl(foundry, deployment) is { } baseUrl
            ? foundry.View(ProviderType.OpenAICompatible, baseUrl, deployment.RequestAuthMode ?? foundry.AuthMode, ProviderCapabilities.Rerank, foundry.Residency)
            : null;

    /// <summary>
    /// Whether <paramref name="url"/> is an absolute address on the same Foundry resource as <paramref name="providerBaseUrl"/>:
    /// the same scheme and port, and the same host, where the resource's <c>…openai.azure.com</c>,
    /// <c>…cognitiveservices.azure.com</c> and <c>…services.ai.azure.com</c> hosts count as one.
    /// </summary>
    public static bool IsOnResource(string providerBaseUrl, string url) =>
        Uri.TryCreate(providerBaseUrl, UriKind.Absolute, out var provider) && Uri.TryCreate(url, UriKind.Absolute, out var candidate)
        && provider.Scheme == candidate.Scheme && provider.Port == candidate.Port && string.IsNullOrEmpty(candidate.UserInfo)
        && string.Equals(ServicesHost(provider.Host), ServicesHost(candidate.Host), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The provider as the gateway calls <paramref name="deployment"/>: a Foundry reranker through <see cref="RerankView"/>
    /// (the provider itself, which serves no Rerank, when it has no address), Claude on Foundry through
    /// <see cref="AnthropicView"/>, anything else through the provider; with the deployment's own residency when it has one
    /// (<see cref="ModelDeployment.Residency"/>). <paramref name="provider"/> is returned itself when nothing differs.
    /// </summary>
    public static ProviderAccount Routed(ProviderAccount provider, ModelDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        var view = IsRerank(provider, deployment) ? RerankView(provider, deployment) ?? provider
            : IsClaude(provider, deployment) ? AnthropicView(provider)
            : provider;
        return deployment.Residency is { } residency && residency != view.Residency
            ? view.View(view.Type, view.BaseUrl, view.AuthMode, view.Capabilities, residency)
            : view;
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
