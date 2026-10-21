using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>How long authenticated key metadata is cached in memory. Revocations invalidate immediately via pub/sub.</summary>
    [Range(0, 300)]
    public int KeyCacheSeconds { get; set; } = 30;

    /// <summary>
    /// Failed authentications (missing or unknown key) one client address may cause per minute before the gateway stops
    /// looking keys up for it: until the minute ends only keys already in the cache are accepted from that address, and
    /// the rest get 429. Protects the database from floods of made-up keys. IPv6 clients count per /64. 0 turns it off.
    /// </summary>
    [Range(0, 1_000_000)]
    public int AuthFailuresPerMinute { get; set; } = 60;

    /// <summary>How long providers/models/routes/budgets are cached. Admin changes invalidate immediately via pub/sub.</summary>
    [Range(0, 300)]
    public int CatalogCacheSeconds { get; set; } = 30;

    /// <summary>Output tokens assumed for budget reservation when the client does not set max_tokens.</summary>
    [Range(1, 1_000_000)]
    public int DefaultOutputTokenEstimate { get; set; } = 1024;

    [Range(1024, 100 * 1024 * 1024)]
    public long MaxRequestBodyBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Upload limit for the audio endpoints (file plus form fields). OpenAI and Azure accept files up to 25 MB; raise it
    /// for on-prem Whisper servers taking longer recordings. The file is held in memory while the request runs.
    /// </summary>
    [Range(1024, 512L * 1024 * 1024)]
    public long MaxAudioRequestBodyBytes { get; set; } = 26 * 1024 * 1024;

    /// <summary>
    /// Upload limit for <c>/v1/images/edits</c> (all pictures, the mask and the form fields together). gpt-image-1 accepts
    /// 50 MB per picture. The files are held in memory while the request runs.
    /// </summary>
    [Range(1024, 512L * 1024 * 1024)]
    public long MaxImageRequestBodyBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// SEK per USD used to price requests only while no USD exchange rate has been entered (admin API, exchange rates).
    /// Each catalogue load that uses it logs a warning, and the readiness check reports Degraded.
    /// </summary>
    [Range(typeof(decimal), "0.01", "1000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal FallbackSekPerUsd { get; set; } = 10m;

    /// <summary>Public documentation URL used in error responses (<c>doc_url</c>); empty leaves it out.</summary>
    public string DocsUrl { get; set; } = "/scalar";

    /// <summary>
    /// Whether the OpenAPI document (<c>/openapi/v1.json</c>) and the browsable reference (<c>/scalar</c>) are served. They
    /// describe the public client API and need no key; turn off to give less away. Read at startup.
    /// </summary>
    public bool PublishApiReference { get; set; } = true;

    /// <summary><see cref="DocsUrl"/>, unless it points at this gateway's own reference and that is not published.</summary>
    public string ErrorDocsUrl => PublishApiReference || !DocsUrl.StartsWith('/') ? DocsUrl : "";

    /// <summary>Optional webhook that receives budget threshold alerts (metadata only).</summary>
    public Uri? AlertWebhookUrl { get; set; }

    /// <summary>Security events and authentication failure recording (docs/data-access.md).</summary>
    [ValidateObjectMembers]
    public SecurityEventOptions Security { get; set; } = new();

    /// <summary>Live audio sessions on <c>/v1/realtime</c> and <c>/v1/realtime/translations</c>.</summary>
    [ValidateObjectMembers]
    public RealtimeOptions Realtime { get; set; } = new();
}

public sealed class RealtimeOptions
{
    /// <summary>Open sessions per key across all instances; 0 means no limit. Each session holds a provider connection.</summary>
    [Range(0, 10_000)]
    public int MaxSessionsPerKey { get; set; } = 20;

    /// <summary>A session is closed after this long (providers have their own limit, e.g. 60 minutes at OpenAI and Azure).</summary>
    [Range(1, 24 * 60)]
    public int MaxSessionMinutes { get; set; } = 120;

    /// <summary>How often a running session's cost is checked against its budgets (and its session slot renewed).</summary>
    [Range(1, 600)]
    public int BudgetCheckSeconds { get; set; } = 30;

    /// <summary>How long a session's slot is held without renewal: a few missed checks, so slots of a crashed instance free themselves.</summary>
    public TimeSpan SessionSlotLifetime => TimeSpan.FromSeconds(BudgetCheckSeconds * 3 + 30);

    /// <summary>Minutes of audio reserved against the budgets at a time: at connect, then again whenever the reservation runs low.</summary>
    [Range(1, 60)]
    public int ReserveMinutes { get; set; } = 5;

    /// <summary>Largest single event either side may send. Audio is sent in small chunks (100 ms of PCM16 is about 6 kB).</summary>
    [Range(64 * 1024, 64 * 1024 * 1024)]
    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class SecurityEventOptions
{
    /// <summary>How much of the client address is kept for authentication failures. The default keeps the network only.</summary>
    public SourceAddressMode SourceAddress { get; set; } = SourceAddressMode.Truncated;

    /// <summary>How often counted authentication failures are written to the database and logged as security events.</summary>
    [Range(1, 3600)]
    public int AuthFailureFlushSeconds { get; set; } = 10;

    /// <summary>Distinct (reason, endpoint, key, address) buckets kept per interval; more are folded into one overflow bucket.</summary>
    [Range(10, 100_000)]
    public int MaxAuthFailureBuckets { get; set; } = 1000;
}
