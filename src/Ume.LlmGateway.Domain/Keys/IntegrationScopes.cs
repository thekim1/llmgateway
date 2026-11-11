namespace Ume.LlmGateway.Domain.Keys;

/// <summary>
/// What a credential key may do on the admin API's Integration API (docs/integrations.md), set per key by gateway
/// admins and empty by default. Values are only ever added.
/// </summary>
public static class IntegrationScopes
{
    /// <summary>Usage and budget status of the integration's own keys.</summary>
    public const string UsageRead = "usage.read";

    public static IReadOnlyList<string> All { get; } = [UsageRead];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);
}
