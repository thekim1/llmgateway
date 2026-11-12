namespace Ume.LlmGateway.Domain.Keys;

/// <summary>
/// What a credential key may read beyond its integration's own keys (docs/integrations.md): on the admin API's
/// Integration API, or in <c>/v1/ume/context</c>. Set per key by gateway admins and empty by default. Values are only
/// ever added.
/// </summary>
public static class IntegrationScopes
{
    /// <summary>Usage and budget status of the integration's own keys.</summary>
    public const string UsageRead = "usage.read";

    /// <summary>Every department and team in <c>/v1/ume/context</c>, not only those of the integration's keys.</summary>
    public const string OrganisationRead = "organisation.read";

    public static IReadOnlyList<string> All { get; } = [UsageRead, OrganisationRead];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);
}
