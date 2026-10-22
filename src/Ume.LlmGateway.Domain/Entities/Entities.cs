using System.Collections.Frozen;
using System.Net;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Entities;

/// <summary>Förvaltning – top level of cost attribution.</summary>
public sealed class Department
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }

    /// <summary>Ansvarskod / cost-centre code used for internal invoicing.</summary>
    public required string CostCenterCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<Team> Teams { get; set; } = [];
}

public sealed class Team
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<VirtualKey> VirtualKeys { get; set; } = [];

    /// <summary>Applies to every key of the team; a key's own policy wins where it sets something.</summary>
    public RequestPolicy? RequestPolicy { get; set; }
}

/// <summary>
/// A gateway API key handed to an application. Only an HMAC hash of the secret is stored;
/// the plaintext is shown once at creation/rotation.
/// </summary>
public sealed class VirtualKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Non-secret identifying prefix, e.g. <c>ume-sk-AbC1</c>, shown in UI and logs.</summary>
    public required string Prefix { get; set; }

    /// <summary>Hex HMAC-SHA256 of the full key using the server-side pepper.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Data-Protection-encrypted full key so gateway admins can reveal it later. Null for keys created before this was stored.</summary>
    public string? EncryptedSecret { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when the key was rotated with a grace period; key works until this instant.</summary>
    public DateTimeOffset? GraceUntil { get; set; }

    public Guid? RotatedToKeyId { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Allowed route aliases / model names. Empty = all.</summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>Allowed provider residencies. Empty = all.</summary>
    public List<DataResidency> AllowedResidencies { get; set; } = [];

    /// <summary>Allowed provider account names (<see cref="ProviderAccount.Name"/>). Empty = all providers.</summary>
    public List<string> AllowedProviders { get; set; } = [];

    /// <summary>
    /// Client addresses the key may be used from: single addresses or CIDR networks in canonical form
    /// (<see cref="Services.NetworkRanges"/>). Empty = any address. Behind a reverse proxy this only works once the proxy is
    /// listed in <see cref="GatewaySettings.TrustedProxies"/>.
    /// </summary>
    public List<string> AllowedIpRanges { get; set; } = [];

    public PiiPolicy PiiPolicy { get; set; } = PiiPolicy.Off;
    public AttachmentPolicy AttachmentPolicy { get; set; } = AttachmentPolicy.Allowed;

    /// <summary>
    /// Whether requests may use tools the provider runs itself (web search, file search, code interpreter, MCP, web fetch),
    /// which send content to third parties or the internet. Off: only tools the client runs (<see cref="Services.ProviderFeaturePolicy"/>).
    /// </summary>
    public bool AllowProviderTools { get; set; }

    /// <summary>
    /// Whether requests may ask the provider to keep the conversation (<c>store</c>, background responses, conversations).
    /// Off: refused, and Responses API requests are sent with <c>store: false</c>.
    /// </summary>
    public bool AllowProviderStorage { get; set; }

    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }

    /// <summary>Requests of this key in flight at once, across all gateway instances; null = no limit. Live audio sessions have their own limit.</summary>
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>Output-token cap, system prompt, parameter defaults and overrides; on top of the team's.</summary>
    public RequestPolicy? RequestPolicy { get; set; }

    public KeyStatus GetStatus(DateTimeOffset now)
    {
        if (RevokedAt is { } revoked && revoked <= now)
        {
            return KeyStatus.Revoked;
        }

        if (!IsEnabled)
        {
            return KeyStatus.Disabled;
        }

        if (ExpiresAt is { } expires && expires <= now)
        {
            return KeyStatus.Expired;
        }

        if (GraceUntil is { } grace)
        {
            return grace > now ? KeyStatus.InGracePeriod : KeyStatus.Revoked;
        }

        return KeyStatus.Active;
    }

    public bool IsUsable(DateTimeOffset now) => GetStatus(now) is KeyStatus.Active or KeyStatus.InGracePeriod;

    // Case-insensitive index of AllowedModels and parsed AllowedIpRanges, built once by IndexAllowLists for keys held in a read-only cache.
    private FrozenSet<string>? _allowedModelIndex;
    private List<string>? _indexedModels;
    private int _indexedCount;
    private IPNetwork[]? _allowedNetworks;
    private List<string>? _indexedRanges;
    private int _indexedRangeCount;

    /// <summary>
    /// Builds the lookups <see cref="IsModelAllowed"/> and <see cref="IsAddressAllowed"/> use, so a cached key answers without
    /// allocating or parsing. Call once the key is loaded for read-only use; if a list is replaced or resized afterwards its
    /// index is ignored.
    /// </summary>
    public void IndexAllowLists()
    {
        _allowedModelIndex = AllowedModels.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _indexedModels = AllowedModels;
        _indexedCount = AllowedModels.Count;
        _allowedNetworks = NetworkRanges.ParseValid(AllowedIpRanges);
        _indexedRanges = AllowedIpRanges;
        _indexedRangeCount = AllowedIpRanges.Count;
    }

    /// <summary>
    /// The key's address allow-list. Empty = every address. An unknown address (no connection address) is refused when a
    /// list is set, and so is every address if no entry parses: a broken list must not open the key to everyone.
    /// </summary>
    public bool IsAddressAllowed(IPAddress? address)
    {
        if (AllowedIpRanges.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        var networks = _allowedNetworks is { } index && ReferenceEquals(_indexedRanges, AllowedIpRanges) && _indexedRangeCount == AllowedIpRanges.Count
            ? index
            : NetworkRanges.ParseValid(AllowedIpRanges);
        return NetworkRanges.Contains(networks, address);
    }

    /// <summary>The key's model allow-list (route aliases and model names, case-insensitive). Empty = every model.</summary>
    public bool IsModelAllowed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (AllowedModels.Count == 0)
        {
            return true;
        }

        return _allowedModelIndex is { } index && ReferenceEquals(_indexedModels, AllowedModels) && _indexedCount == AllowedModels.Count
            ? index.Contains(name)
            : AllowedModels.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A key for the read-only Data API (docs/data-access.md), for integrations that cannot use OAuth client credentials.
/// Managed by gateway admins; holds only the hash of the key (<see cref="Services.DataApiKeyHasher"/>), never the key.
/// </summary>
public sealed class DataApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Non-secret identifying prefix, e.g. <c>ume-dk-AbC1</c>, shown in the UI.</summary>
    public required string Prefix { get; set; }

    /// <summary>Hex SHA-256 of the full key.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Granted Data API permissions (<see cref="DataPermissions"/>).</summary>
    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Written by the Data API at most every few minutes per key, so it is approximate.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Active, Expired or Revoked.</summary>
    public KeyStatus GetStatus(DateTimeOffset now) =>
        RevokedAt is { } revoked && revoked <= now ? KeyStatus.Revoked
        : ExpiresAt is { } expires && expires <= now ? KeyStatus.Expired
        : KeyStatus.Active;
}

public sealed class ProviderAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Unique slug, e.g. <c>ollama-local</c>, <c>azure-swc</c>.</summary>
    public required string Name { get; set; }

    public string? DisplayName { get; set; }
    public ProviderType Type { get; set; }

    /// <summary>Base URL including version segment, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; set; }

    public ProviderAuthMode AuthMode { get; set; }

    /// <summary>Credential encrypted with ASP.NET Data Protection. Never returned by any API.</summary>
    public string? EncryptedCredential { get; set; }

    public DataResidency Residency { get; set; }
    public ProviderCapabilities Capabilities { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Operator flag: take provider out of rotation without deleting configuration.</summary>
    public bool IsDrained { get; set; }

    public int TimeoutSeconds { get; set; } = 120;
    public DateTimeOffset CreatedAt { get; set; }
    public List<ModelDeployment> Deployments { get; set; } = [];

    public bool IsAvailable => IsEnabled && !IsDrained;

    public bool Supports(GatewayEndpoint endpoint) => GatewayEndpoints.Info(endpoint).IsSupportedBy(Capabilities);
}

/// <summary>A concrete model offered by a provider account.</summary>
public sealed class ModelDeployment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProviderAccountId { get; set; }
    public ProviderAccount? ProviderAccount { get; set; }

    /// <summary>Gateway-visible name, unique, e.g. <c>azure-swc/gpt-4o-mini</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Model id or Azure deployment name sent upstream.</summary>
    public required string UpstreamModel { get; set; }

    public ModelKind Kind { get; set; }
    public ParameterProfile ParameterProfile { get; set; }
    public int? ContextWindow { get; set; }
    /// <summary>What the model can do, e.g. tools, vision, thinking, embedding, image generation. Free-form tags.</summary>
    public string[] Features { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
    public List<ModelPrice> Prices { get; set; } = [];

    public ModelPrice? PriceAt(DateTimeOffset when) =>
        Prices.Where(p => p.EffectiveFrom <= when).MaxBy(p => p.EffectiveFrom);
}

/// <summary>
/// Price per one million tokens in USD, valid from <see cref="EffectiveFrom"/>. Image models billed per picture use
/// <see cref="PerImageUsd"/>. Speech-to-text models billed by
/// duration (Whisper, gpt-realtime-translate) use <see cref="AudioPerMinuteUsd"/> instead; a model may have both.
/// Audio tokens (gpt-realtime, gpt-4o-transcribe) cost more than text tokens: when an audio token price is set, the
/// audio part of the tokens is billed at it and only the rest at the text price.
/// </summary>
public sealed class ModelPrice
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ModelDeploymentId { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public decimal InputPerMillionUsd { get; set; }
    public decimal CachedInputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }

    /// <summary>
    /// Price per million input tokens written to the prompt cache in USD (Anthropic: 1.25x input for the 5-minute
    /// cache, 2x for the 1-hour cache). Null bills cache writes at <see cref="InputPerMillionUsd"/>.
    /// </summary>
    public decimal? CacheWriteInputPerMillionUsd { get; set; }

    /// <summary>Price per minute of input audio in USD.</summary>
    public decimal AudioPerMinuteUsd { get; set; }

    /// <summary>Price per million audio input tokens in USD; 0 bills them as text input.</summary>
    public decimal AudioInputPerMillionUsd { get; set; }

    /// <summary>Price per million audio output tokens in USD; 0 bills them as text output.</summary>
    public decimal AudioOutputPerMillionUsd { get; set; }

    /// <summary>Price per generated image in USD (DALL·E, FLUX …). Token-billed image models (gpt-image-1) use the token prices instead; a model may have both.</summary>
    public decimal PerImageUsd { get; set; }
}

/// <summary>Client-facing model alias (e.g. <c>ume/chat-standard</c>) mapping to targets with fallback.</summary>
public sealed class RouteAlias
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public ModelKind Kind { get; set; }
    public bool IsEnabled { get; set; } = true;
    public List<RouteTarget> Targets { get; set; } = [];
}

public sealed class RouteTarget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RouteAliasId { get; set; }
    public Guid ModelDeploymentId { get; set; }
    public ModelDeployment? ModelDeployment { get; set; }

    /// <summary>Lower = tried first. Targets with equal priority are load-balanced by weight.</summary>
    public int Priority { get; set; }

    public int Weight { get; set; } = 1;
}

/// <summary>
/// An administrator-defined routing rule: when <see cref="Condition"/> matches a request, it is sent to
/// <see cref="Targets"/> (weighted) with <see cref="Fallbacks"/> instead of the alias it asked for. Evaluated by
/// <see cref="Routing.RoutingRuleSet"/>. <see cref="ScopeIds"/> are the keys, teams or departments (of the kind
/// <see cref="Scope"/> names) the rule is shared by; no foreign keys: an owner that no longer exists simply never matches.
/// </summary>
public sealed class RoutingRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Lower is checked first within a scope.</summary>
    public int Priority { get; set; }

    public Routing.RoutingScope Scope { get; set; } = Routing.RoutingScope.Global;

    /// <summary>The owners the rule applies to (empty for a global rule, or for a disabled rule waiting for a new owner).</summary>
    public List<Guid> ScopeIds { get; set; } = [];

    /// <summary>A shipped example (disabled until an administrator adopts it). Editing or enabling it makes it an ordinary rule.</summary>
    public bool IsTemplate { get; set; }

    /// <summary>Condition in the routing expression language; empty always matches.</summary>
    public string Condition { get; set; } = "";

    /// <summary>When true the rewritten model is routed through the rules again.</summary>
    public bool Chain { get; set; }

    public List<RoutingRuleTarget> Targets { get; set; } = [];

    /// <summary>Models (alias or deployment names) tried in order after all targets.</summary>
    public List<string> Fallbacks { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Routing.RoutingRuleDefinition ToDefinition() => new(
        Id, Name, IsEnabled, Priority, Scope, [.. ScopeIds], Condition, Chain,
        [.. Targets.Select(t => new Routing.RuleTarget(t.Model, t.Weight))], [.. Fallbacks]);
}

/// <summary>A weighted destination of a <see cref="RoutingRule"/>: a route alias or deployment name.</summary>
public sealed class RoutingRuleTarget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RoutingRuleId { get; set; }
    public required string Model { get; set; }
    public int Weight { get; set; } = 1;
}

public sealed class Budget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public BudgetScope Scope { get; set; }
    public Guid ScopeId { get; set; }

    /// <summary>The limit in USD, the currency costs are calculated and counted in.</summary>
    public decimal LimitUsd { get; set; }
    public BudgetPeriod Period { get; set; }

    /// <summary>Percentages (1-100) at which alerts are raised.</summary>
    public List<int> AlertThresholds { get; set; } = [50, 80, 100];

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Gateway-wide settings edited on the admin UI's Settings page. One row (<see cref="SingletonId"/>); no row means the
/// defaults. The gateway reads it with the catalogue, so a change takes effect when the admin API publishes it.
/// </summary>
public sealed class GatewaySettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Reverse proxies whose <c>X-Forwarded-For</c> the gateway believes: single addresses or CIDR networks in canonical form.
    /// Empty = the connection address is the client address and the header is ignored.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>
    /// Log every request at Information, successful ones too (normally only refused, failed and cancelled requests are).
    /// Costs throughput while on; every request is in the usage records either way.
    /// </summary>
    public bool LogAllRequests { get; set; }

    /// <summary>When <see cref="LogAllRequests"/> switches itself off; null = it stays on until it is turned off.</summary>
    public DateTimeOffset? LogAllRequestsUntil { get; set; }

    /// <summary>Whether every request is logged at <paramref name="now"/>.</summary>
    public bool LogsAllRequestsAt(DateTimeOffset now) => LogAllRequests && (LogAllRequestsUntil is not { } until || now < until);

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Days usage records, refused-credential records and budget alerts are kept (<see cref="RetentionPolicy"/>). Whole
    /// months are removed, after their monthly totals are saved in <see cref="UsageMonthlyTotal"/>.
    /// </summary>
    public int UsageRetentionDays { get; set; } = RetentionPolicy.DefaultDays;

    /// <summary>Days admin audit entries are kept; null keeps them for ever.</summary>
    public int? AuditRetentionDays { get; set; } = RetentionPolicy.DefaultDays;

    /// <summary>
    /// The oldest instant usage records cover (a Europe/Stockholm month start), set by the retention job; null until it
    /// first removes anything. Usage before it is only in <see cref="UsageMonthlyTotal"/>, so reports and budget spend read
    /// totals before it and records from it on. Written only by the database function <c>ume_retention_step</c>.
    /// </summary>
    public DateTimeOffset? UsageRetainedFrom { get; set; }

    public DateTimeOffset? RetentionLastRunAt { get; set; }

    /// <summary>Rows removed by the last run, all tables together.</summary>
    public long? RetentionLastRunDeleted { get; set; }

    /// <summary>Why the last run failed (no exception text, which could hold connection details); null when it succeeded.</summary>
    public string? RetentionLastRunError { get; set; }
}

/// <summary>
/// Usage for one Europe/Stockholm month, key, model, provider and endpoint, kept after the month's usage records are
/// removed by the retention job. Written only by the database function <c>ume_retention_step</c>; never edited.
/// </summary>
public sealed class UsageMonthlyTotal
{
    public long Id { get; set; }

    /// <summary>The month's start (Europe/Stockholm midnight on the 1st) as an instant.</summary>
    public DateTimeOffset MonthStart { get; set; }

    public Guid VirtualKeyId { get; set; }
    public Guid TeamId { get; set; }
    public Guid DepartmentId { get; set; }
    public GatewayEndpoint Endpoint { get; set; }
    public required string RequestedModel { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderName { get; set; }
    public Guid? ModelDeploymentId { get; set; }
    public long Requests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long Fallbacks { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal AudioSeconds { get; set; }
    public decimal CostUsd { get; set; }
    public decimal CostSek { get; set; }
}

public sealed class ExchangeRate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Currency { get; set; } = "USD";
    public decimal SekPerUnit { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
}

/// <summary>
/// Metadata about one gateway request. Deliberately contains NO prompt or response content
/// (GDPR data minimisation). PII is recorded as category counts only.
/// </summary>
public sealed class UsageRecord
{
    public long Id { get; set; }
    public required string RequestId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Guid VirtualKeyId { get; set; }
    public Guid TeamId { get; set; }
    public Guid DepartmentId { get; set; }
    public GatewayEndpoint Endpoint { get; set; }
    public required string RequestedModel { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderName { get; set; }
    public Guid? ModelDeploymentId { get; set; }
    public string? UpstreamModel { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Seconds of audio transcribed (audio endpoints only): as reported by the provider, else estimated from the file.</summary>
    public decimal AudioSeconds { get; set; }

    public decimal CostUsd { get; set; }
    public decimal CostSek { get; set; }
    /// <summary>End to end: from the gateway receiving the request until its answer was sent (a live session's length).</summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Time spent waiting on providers: every attempt (failed fallbacks included) and reading a streamed answer.
    /// <see cref="LatencyMs"/> minus this is the gateway's own overhead. Null when no provider was called and for live sessions.
    /// </summary>
    public int? UpstreamMs { get; set; }

    /// <summary>Streamed answers: from the gateway receiving the request until the first event reached the client.</summary>
    public int? TimeToFirstTokenMs { get; set; }

    public int StatusCode { get; set; }
    public RequestOutcome Outcome { get; set; }
    public int FallbackCount { get; set; }
    public bool Streamed { get; set; }
    public PiiPolicy? PiiActionApplied { get; set; }

    /// <summary>e.g. <c>Personnummer:2,Email:1</c>. Never the matched values.</summary>
    public string? PiiCategories { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>The routing rule that decided where the request went (last one when rules were chained). No foreign key: the rule may be deleted later.</summary>
    public Guid? RoutingRuleId { get; set; }

    /// <summary>Name of that rule at the time of the request, so reports stay readable after it is renamed or deleted.</summary>
    public string? RoutingRuleName { get; set; }

    /// <summary>
    /// When the row was written (database transaction start, set by Postgres). Data API feeds only hand out rows older
    /// than their settle window, so a row committed late by a concurrent writer is never skipped by a consumer's cursor.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }

    /// <summary>JSON details; secrets are always masked before being written.</summary>
    public string? Details { get; set; }

    /// <summary>The actor's display name or e-mail when the entry was written; the subject alone may be gone from the IdP later.</summary>
    public string? ActorName { get; set; }

    /// <summary>The actor's admin roles at the time, comma-separated.</summary>
    public string? ActorRoles { get; set; }

    /// <summary>The actor's department codes at the time, comma-separated.</summary>
    public string? ActorDepartments { get; set; }

    /// <summary>The actor's client address behind trusted proxies, as <c>Admin:AuditSourceAddress</c> keeps it.</summary>
    public string? SourceAddress { get; set; }

    /// <summary>The sign-in session (a random id from sign-in, never the cookie): ties entries of one session together.</summary>
    public string? SessionId { get; set; }

    /// <summary>The request's trace id, to find it in logs and traces.</summary>
    public string? RequestId { get; set; }

    /// <summary>The browser or client, at most 200 characters.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// When the row was written (database transaction start, set by Postgres). Data API feeds only hand out rows older
    /// than their settle window, so a row committed late by a concurrent writer is never skipped by a consumer's cursor.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class AlertEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BudgetId { get; set; }
    public BudgetScope Scope { get; set; }
    public Guid ScopeId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public int ThresholdPercent { get; set; }
    public decimal SpentUsd { get; set; }
    public decimal LimitUsd { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public bool Acknowledged { get; set; }
}

/// <summary>
/// Refused credentials, counted per (reason, endpoint, key, source address) over a short interval instead of one row
/// per request: unauthenticated callers must not be able to grow the table at will. Never holds the presented key.
/// </summary>
public sealed class AuthFailure
{
    public long Id { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public AuthFailureReason Reason { get; set; }
    public GatewayEndpoint Endpoint { get; set; }

    /// <summary>Set only when the presented key was recognised (revoked, expired, disabled, owner inactive).</summary>
    public Guid? VirtualKeyId { get; set; }

    public string? KeyPrefix { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Client address, truncated or omitted according to <c>Gateway:Security:SourceAddress</c>.</summary>
    public string? SourceAddress { get; set; }

    public int Count { get; set; }

    /// <summary>
    /// When the row was written (database transaction start, set by Postgres). Data API feeds only hand out rows older
    /// than their settle window, so a row committed late by a concurrent writer is never skipped by a consumer's cursor.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}
