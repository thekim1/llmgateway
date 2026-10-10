using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Ume.LlmGateway.DataApi;

/// <summary>Settings for the Data API (docs/data-access.md). Defaults expose the least.</summary>
public sealed class DataApiOptions
{
    public const string SectionName = "DataApi";

    /// <summary>OIDC issuer whose access tokens are accepted (the organisation's Keycloak realm, AD FS or Entra ID tenant).</summary>
    public string? Authority { get; set; }

    /// <summary>Required <c>aud</c> of access tokens.</summary>
    [Required]
    public string Audience { get; set; } = "ume-data-api";

    /// <summary>
    /// Claims that carry the client's permissions (<c>usage.aggregate</c> and so on). <c>scope</c>/<c>scp</c> are
    /// space-separated OAuth scopes; <c>roles</c> is what Entra ID uses for application permissions.
    /// </summary>
    public List<string> PermissionClaims { get; set; } = ["scope", "scp", "roles"];

    /// <summary>
    /// Feeds only return rows written longer ago than this. Concurrent writers commit out of id order; waiting lets
    /// every row with a lower id become visible before a consumer's cursor passes it.
    /// </summary>
    [Range(0, 3600)]
    public int SettleSeconds { get; set; } = 60;

    [Range(1, 50_000)]
    public int MaxPageSize { get; set; } = 10_000;

    [Range(1, 3660)]
    public int MaxAggregateDays { get; set; } = 400;

    /// <summary>Aggregate rows for a department with fewer distinct keys in the period are reported without the department.</summary>
    [Range(1, 1000)]
    public int MinimumGroupSize { get; set; } = 5;

    /// <summary>Day and month boundaries of aggregates (IANA id). Defaults to the zone budgets use.</summary>
    [Required]
    public string TimeZone { get; set; } = "Europe/Stockholm";

    /// <summary>Requests per minute per client.</summary>
    [Range(1, 100_000)]
    public int RequestsPerMinute { get; set; } = 600;

    [ValidateObjectMembers]
    public DetailOptions Detail { get; set; } = new();

    public sealed class DetailOptions
    {
        /// <summary>Whether <c>usage.detail</c> includes PII category counts (<c>security.read</c> always has them).</summary>
        public bool IncludePiiCategories { get; set; }
    }
}

/// <summary>Permissions a client can be granted; each is one consumer class.</summary>
public static class DataPermissions
{
    public const string UsageAggregate = "usage.aggregate";
    public const string UsageDetail = "usage.detail";
    public const string SecurityRead = "security.read";
    public const string CatalogRead = "catalog.read";

    public static readonly string[] All = [UsageAggregate, UsageDetail, SecurityRead, CatalogRead];
}
