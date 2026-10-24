using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Infrastructure.Providers;
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
    /// How the first target among targets of the same priority is chosen: <c>Weighted</c> (default, shares follow the
    /// weights) or <c>LeastLoaded</c> (the faster, healthier, less busy of two drawn by weight; see docs/routing-rules.md).
    /// </summary>
    public LoadBalancingMode LoadBalancing { get; set; } = LoadBalancingMode.Weighted;

    /// <summary>
    /// Prompt-cache markers added to OpenAI-format requests the gateway translates for Anthropic: <c>Off</c>,
    /// <c>Static</c> (default: tools and system prompt) or <c>Conversation</c> (also the chat so far). Requests sent to
    /// <c>/v1/messages</c> keep the client's own markers.
    /// </summary>
    public PromptCacheMode PromptCaching { get; set; } = PromptCacheMode.Static;

    /// <summary>
    /// Also record the OpenTelemetry GenAI semantic-convention metrics (<c>gen_ai.client.token.usage</c>,
    /// <c>gen_ai.client.operation.duration</c>, <c>gen_ai.server.request.duration</c>,
    /// <c>gen_ai.server.time_to_first_token</c>) for standard GenAI dashboards. Off by default: they cost four histogram
    /// recordings per request, and <c>ume.gateway.*</c> covers the same ground.
    /// </summary>
    public bool GenAiMetrics { get; set; }

    /// <summary>The on-prem content guard for keys whose policy is not Off (see <see cref="ContentGuardOptions"/>).</summary>
    [ValidateObjectMembers]
    public ContentGuardOptions ContentGuard { get; set; } = new();

    /// <summary>Where usage records wait while the database cannot take them (see <see cref="UsageJournalOptions"/>).</summary>
    [ValidateObjectMembers]
    public UsageJournalOptions UsageJournal { get; set; } = new();

    /// <summary>Retries of the last candidate after a passing failure (see <see cref="RetryOptions"/>).</summary>
    [ValidateObjectMembers]
    public RetryOptions Retry { get; set; } = new();

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

/// <summary>
/// When the last candidate fails with a connection error or 502/503/504 (no <c>Retry-After</c>, not a timeout), it is
/// tried again after a short, jittered, doubling delay. Earlier candidates are not retried: the next one is the retry.
/// Never after a response has started.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>Extra attempts on the last candidate; 0 turns retries off.</summary>
    [Range(0, 3)]
    public int MaxRetries { get; set; } = 1;

    /// <summary>Delay before the first retry; each further retry doubles it. ±50 % jitter.</summary>
    [Range(10, 10_000)]
    public int BaseDelayMs { get; set; } = 250;
}

/// <summary>When the usage writer journals batches of usage records (in Redis), so they are not lost.</summary>
public enum UsageJournalMode
{
    /// <summary>Never: during a database outage the queue fills, requests wait and records are dropped after 10 s (as before).</summary>
    Off = 0,

    /// <summary>
    /// Only when the database refuses a batch (default): while it is down batches go to the journal, so requests are not
    /// held up and nothing is dropped; they are written when it is back. No extra work while the database is healthy; a
    /// crash then loses at most the batch being written.
    /// </summary>
    Spill = 1,

    /// <summary>Every batch, before it is written: a crash loses nothing, at the cost of one more Redis write per batch.</summary>
    Always = 2,
}

public sealed class UsageJournalOptions
{
    public UsageJournalMode Mode { get; set; } = UsageJournalMode.Spill;

    /// <summary>Most records the journal holds (about 1 KB each in Redis); beyond it the writer holds batches in memory as before.</summary>
    [Range(1_000, 100_000_000)]
    public long MaxRecords { get; set; } = 1_000_000;

    /// <summary>A journaled batch whose instance has not touched it for this long is taken over by another instance.</summary>
    [Range(30, 3600)]
    public int AbandonedAfterSeconds { get; set; } = 120;
}

/// <summary>
/// The content guard (docs/content-guard.md): rules in the gateway and, optionally, a guard model the gateway calls on an
/// on-prem provider. Applies only to keys whose content guard policy is Record or Block.
/// </summary>
public sealed class ContentGuardOptions
{
    /// <summary>
    /// The guard model's name in the catalogue (e.g. <c>onprem/llama-guard3</c>), served by an on-prem provider; empty uses
    /// the rules only. A deployment whose provider is not on-prem is never used.
    /// </summary>
    [StringLength(200)]
    public string? Model { get; set; }

    /// <summary>How the guard model answers: <c>LlamaGuard</c> (safe/unsafe) or <c>GraniteGuardian</c> (Yes/No).</summary>
    public Domain.Services.GuardModelFormat Format { get; set; }

    /// <summary>Also check complete (non-streamed) answers with the guard model and the blocked patterns.</summary>
    public bool CheckAnswers { get; set; }

    /// <summary>The longest the guard model may take; longer counts as unavailable.</summary>
    [Range(100, 60_000)]
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// When the guard model is unavailable (down, too slow, unreadable answer): true lets the request through and records
    /// <c>guard_unavailable</c>; false refuses requests of keys with policy Block (503 <c>content_guard_unavailable</c>).
    /// </summary>
    public bool FailOpen { get; set; } = true;

    /// <summary>Check the built-in prompt-injection phrasings (English and Swedish).</summary>
    public bool PromptInjectionRules { get; set; } = true;

    /// <summary>An administrator's regular expressions (case-insensitive); a match is <c>blocked_pattern</c>.</summary>
    [MaxLength(200)]
    public List<string> BlockedPatterns { get; set; } = [];

    /// <summary>How much text is checked: the last this many characters of the request (the most recent turns) or answer.</summary>
    [Range(1000, 1_000_000)]
    public int MaxCharacters { get; set; } = 16_000;
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
