namespace Ume.LlmGateway.AdminApi;

/// <summary>
/// Azure AI Foundry addresses. The portal shows the project endpoint
/// (<c>https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>), but requests go to the resource's
/// OpenAI v1 API (<c>…/openai/v1</c>), and only the project API lists the deployments: <c>/openai/v1/models</c> lists
/// every model the resource could deploy.
/// </summary>
public static class FoundryEndpoint
{
    private const string ProjectsPath = "/api/projects/";
    private const string ServicesHostSuffix = ".services.ai.azure.com";
    private static readonly string[] ResourceHostSuffixes = [".openai.azure.com", ".cognitiveservices.azure.com", ServicesHostSuffix];

    /// <summary>The base URL requests are sent to: a project endpoint or a bare resource address becomes <c>…/openai/v1</c>.</summary>
    public static string InferenceBaseUrl(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return baseUrl;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Length == 0 || IsProjectPath(path) ? uri.GetLeftPart(UriPartial.Authority) + "/openai/v1" : baseUrl;
    }

    /// <summary>
    /// The project API's deployment list for the resource behind <paramref name="baseUrl"/>. Deployments belong to the
    /// resource, so the default project (<c>_project</c>) lists them unless the base URL names a project.
    /// </summary>
    public static Uri DeploymentsUri(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var uri = new Uri(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        var project = IsProjectPath(path) ? path[ProjectsPath.Length..].Split('/')[0] : "_project";
        var host = uri.Host;
        var suffix = ResourceHostSuffixes.FirstOrDefault(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (suffix is not null)
        {
            host = host[..^suffix.Length] + ServicesHostSuffix;
        }

        return new UriBuilder(uri.Scheme, host, uri.Port, $"{ProjectsPath}{project}/deployments") { Query = "api-version=v1" }.Uri;
    }

    /// <summary>Whether <paramref name="baseUrl"/> is on a Foundry resource (<c>…services.ai.azure.com</c>).</summary>
    public static bool IsFoundryHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host.EndsWith(ServicesHostSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectPath(string path) =>
        path.StartsWith(ProjectsPath, StringComparison.OrdinalIgnoreCase) && path.Length > ProjectsPath.Length;
}
