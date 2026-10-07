using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.Gateway.Pipeline;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>How long authenticated key metadata is cached in memory. Revocations invalidate immediately via pub/sub.</summary>
    [Range(0, 300)]
    public int KeyCacheSeconds { get; set; } = 30;

    /// <summary>How long providers/models/routes/budgets are cached. Admin changes invalidate immediately via pub/sub.</summary>
    [Range(0, 300)]
    public int CatalogCacheSeconds { get; set; } = 30;

    /// <summary>Output tokens assumed for budget reservation when the client does not set max_tokens.</summary>
    [Range(1, 1_000_000)]
    public int DefaultOutputTokenEstimate { get; set; } = 1024;

    [Range(1024, 100 * 1024 * 1024)]
    public long MaxRequestBodyBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Public documentation URL used in error responses.</summary>
    public string DocsUrl { get; set; } = "/scalar";

    /// <summary>Optional webhook that receives budget threshold alerts (metadata only).</summary>
    public Uri? AlertWebhookUrl { get; set; }
}
