namespace Ume.LlmGateway.Domain.Services;

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
        var host = uri.Host;
        var suffix = ResourceHostSuffixes.FirstOrDefault(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (suffix is not null)
        {
            host = host[..^suffix.Length] + ServicesHostSuffix;
        }

        return new UriBuilder(uri.Scheme, host, uri.Port, $"{ProjectsPath}{ProjectName(baseUrl) ?? "_project"}/deployments") { Query = "api-version=v1" }.Uri;
    }

    /// <summary>The project named in a project endpoint, or <c>null</c>.</summary>
    public static string? ProjectName(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/') is var path && IsProjectPath(path)
            ? path[ProjectsPath.Length..].Split('/')[0]
            : null;

    /// <summary>Whether <paramref name="baseUrl"/> is on a Foundry resource (<c>…services.ai.azure.com</c>).</summary>
    public static bool IsFoundryHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && IsFoundryHost(uri);

    private static bool IsFoundryHost(Uri uri) => uri.Host.EndsWith(ServicesHostSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectPath(string path) =>
        path.StartsWith(ProjectsPath, StringComparison.OrdinalIgnoreCase) && path.Length > ProjectsPath.Length;
}
