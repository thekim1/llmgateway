namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// Resource URIs on the aggregated <c>/mcp</c>: <c>mcp+ume://{slug}/{original-uri}</c>, so a read can be sent to the
/// right server. The original URI follows the slug unchanged; <c>/mcp/{slug}</c> uses original URIs.
/// </summary>
public static class McpResourceUris
{
    public const string Scheme = "mcp+ume://";

    public static string Namespace(string slug, string uri)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(uri);
        return Scheme + slug + "/" + uri;
    }

    /// <summary>Splits a namespaced URI into its server slug and original URI; false when it is not one.</summary>
    public static bool TryParse(string? namespaced, out string slug, out string uri)
    {
        slug = uri = string.Empty;
        if (namespaced is null || !namespaced.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = namespaced.AsSpan(Scheme.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1 || !McpToolNames.IsValidSlug(rest[..slash].ToString()))
        {
            return false;
        }

        slug = rest[..slash].ToString();
        uri = rest[(slash + 1)..].ToString();
        return true;
    }

    /// <summary>The server slug an exposed name (<see cref="McpToolNames.Exposed"/>) starts with: everything before the first <c>__</c>.</summary>
    public static string? SlugOfExposedName(string exposed)
    {
        ArgumentNullException.ThrowIfNull(exposed);
        var separator = exposed.IndexOf(McpToolNames.Separator, StringComparison.Ordinal);
        return separator > 0 && McpToolNames.IsValidSlug(exposed[..separator]) ? exposed[..separator] : null;
    }
}
