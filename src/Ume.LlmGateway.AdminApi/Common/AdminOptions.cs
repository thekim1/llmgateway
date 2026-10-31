using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Common;

/// <summary>Bound from the <c>Admin</c> configuration section.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Requests per minute per signed-in user (or client address before sign-in).</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// Hours a session may last from sign-in, however often it is extended. After that the user signs in at the identity
    /// provider again (which also picks up role changes made there).
    /// </summary>
    [Range(1, 24)]
    public int MaxSessionHours { get; set; } = 12;

    /// <summary>
    /// How much of an admin user's address audit entries and admin security events keep: <c>Full</c> (default; an
    /// investigation needs it), <c>Truncated</c> (IPv4 /24, IPv6 /48) or <c>None</c>.
    /// </summary>
    public SourceAddressMode AuditSourceAddress { get; set; } = SourceAddressMode.Full;

    /// <summary>The Azure region (<c>armRegionName</c>) whose list prices are suggested for Azure AI Foundry deployments.</summary>
    [RegularExpression("^[a-z0-9]{2,40}$")]
    public string AzurePriceRegion { get; set; } = "swedencentral";

    /// <summary>
    /// Where model list prices are read, once a day: models.dev's <c>api.json</c> or a copy of it (for an admin API without
    /// internet access). Empty turns list prices off.
    /// </summary>
    [RegularExpression("^https?://.+")]
    public string? PriceListUrl { get; set; } = "https://models.dev/api.json";

    /// <summary>
    /// Add a changed list price to the price history of every model whose price is not locked, once a day. Off, list prices are
    /// only suggested when models are discovered.
    /// </summary>
    public bool AutoUpdatePrices { get; set; } = true;

    /// <summary>Whether the admin API asks GitHub for the latest release, shown in Settings. Turn off where the admin API has no internet access.</summary>
    public bool CheckForReleases { get; set; } = true;

    /// <summary>The GitHub repository (<c>owner/name</c>) whose releases are checked.</summary>
    [RegularExpression("^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    public string ReleaseRepository { get; set; } = "thekim1/llmgateway";
}
