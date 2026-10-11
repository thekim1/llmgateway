namespace Ume.LlmGateway.Domain;

/// <summary>Data API permissions a client (OIDC client or Data API key) can be granted; each is one consumer class (docs/data-access.md).</summary>
public static class DataPermissions
{
    public const string UsageAggregate = "usage.aggregate";
    public const string UsageDetail = "usage.detail";
    public const string SecurityRead = "security.read";
    public const string CatalogRead = "catalog.read";

    public static readonly string[] All = [UsageAggregate, UsageDetail, SecurityRead, CatalogRead];

    public static bool IsKnown(string permission) => All.Contains(permission, StringComparer.Ordinal);
}
