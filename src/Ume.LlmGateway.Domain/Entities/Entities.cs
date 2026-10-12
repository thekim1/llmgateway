using System.Collections.Frozen;

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

    public PiiPolicy PiiPolicy { get; set; } = PiiPolicy.Off;
    public AttachmentPolicy AttachmentPolicy { get; set; } = AttachmentPolicy.Allowed;
    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }

    /// <summary>MCP calls sent to MCP servers per minute (tool calls, resource reads, prompts). Null = no limit.</summary>
    public int? McpCallsPerMinute { get; set; }

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

    // Case-insensitive index of AllowedModels, built once by IndexAllowedModels for keys held in a read-only cache.
    private FrozenSet<string>? _allowedModelIndex;
    private List<string>? _indexedModels;
    private int _indexedCount;

    /// <summary>
    /// Builds the lookup <see cref="IsModelAllowed"/> uses, so a cached key answers without allocating. Call once the key is
    /// loaded for read-only use; if <see cref="AllowedModels"/> is replaced or resized afterwards the index is ignored.
    /// </summary>
    public void IndexAllowedModels()
    {
        _allowedModelIndex = AllowedModels.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _indexedModels = AllowedModels;
        _indexedCount = AllowedModels.Count;
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
/// Price per one million tokens in USD, valid from <see cref="EffectiveFrom"/>. Speech-to-text models billed by
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

    /// <summary>Price per minute of input audio in USD.</summary>
    public decimal AudioPerMinuteUsd { get; set; }

    /// <summary>Price per million audio input tokens in USD; 0 bills them as text input.</summary>
    public decimal AudioInputPerMillionUsd { get; set; }

    /// <summary>Price per million audio output tokens in USD; 0 bills them as text output.</summary>
    public decimal AudioOutputPerMillionUsd { get; set; }
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

/// <summary>
/// A remote MCP server (Streamable HTTP) the gateway exposes on <c>/mcp</c> and <c>/mcp/{slug}</c>. Keys reach it only
/// through a <see cref="VirtualKeyMcpGrant"/>, and only its approved tools are visible.
/// </summary>
public sealed class McpServer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Unique, lower-case letters and digits with single underscores between them, at most 24 characters
    /// (<see cref="Services.McpToolNames.IsValidSlug"/>); prefixes tool names on the aggregated <c>/mcp</c>.
    /// </summary>
    public required string Slug { get; set; }

    public required string DisplayName { get; set; }
    public string? Description { get; set; }

    /// <summary>The server's Streamable HTTP endpoint, e.g. <c>https://diarium.internal/mcp</c>.</summary>
    public required string Url { get; set; }

    /// <summary>Where the server processes data; checked against <see cref="VirtualKey.AllowedResidencies"/>.</summary>
    public DataResidency Residency { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>The department that manages the server; null for a global server.</summary>
    public Guid? OwnerDepartmentId { get; set; }

    public McpAuthType AuthType { get; set; }

    /// <summary>
    /// Encrypted with ASP.NET Data Protection, never returned by any API: a JSON object of header names and values for
    /// <see cref="McpAuthType.StaticHeaders"/>, or the token endpoint, client id, secret and scope for <see cref="McpAuthType.OAuthClientCredentials"/>.
    /// </summary>
    public string? EncryptedCredential { get; set; }

    /// <summary>Caller headers sent on to the server, by exact name. Empty = none.</summary>
    public List<string> ForwardHeaders { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 30;
    public int MaxResultBytes { get; set; } = 1024 * 1024;

    /// <summary>Trusted internal servers only: newly discovered tools are approved without an admin.</summary>
    public bool AutoApproveNewTools { get; set; }

    public McpServerStatus Status { get; set; } = McpServerStatus.Unavailable;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }
    public McpServerCapabilities Capabilities { get; set; }

    /// <summary>The server's own instructions, capped at <see cref="MaxInstructionsLength"/> characters.</summary>
    public string? Instructions { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public List<McpTool> Tools { get; set; } = [];

    public const int MaxInstructionsLength = 4096;
}

/// <summary>
/// One tool discovered on an <see cref="McpServer"/>. <see cref="Definition"/> is what the server offers now;
/// clients only ever see <see cref="ApprovedDefinition"/>, so a changed upstream definition waits for re-approval.
/// </summary>
public sealed class McpTool
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid McpServerId { get; set; }

    /// <summary>The tool's name on the server (case-sensitive).</summary>
    public required string Name { get; set; }

    public string? Title { get; set; }

    /// <summary>Hex SHA-256 of <see cref="Definition"/>, compared on every sync.</summary>
    public required string DefinitionHash { get; set; }

    /// <summary>JSON: description, inputSchema, outputSchema and annotations as the server last reported them.</summary>
    public required string Definition { get; set; }

    public McpToolApprovalState ApprovalState { get; set; }

    /// <summary>JSON in the same shape as <see cref="Definition"/>: the definition clients see. Null until first approved.</summary>
    public string? ApprovedDefinition { get; set; }

    /// <summary>The name on the aggregated <c>/mcp</c> and on the LLM path (<see cref="Services.McpToolNames.Exposed"/>), unique across servers.</summary>
    public required string ExposedName { get; set; }

    /// <summary>Price per call in SEK, charged to budgets. Null = free.</summary>
    public decimal? PriceSek { get; set; }

    public DateTimeOffset DiscoveredAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    /// <summary>Whether clients see the tool: it was approved, and a later upstream change does not hide it.</summary>
    public bool IsExposed => ApprovalState is (McpToolApprovalState.Approved or McpToolApprovalState.Changed) && ApprovedDefinition is not null;
}

/// <summary>
/// Lets a virtual key use an <see cref="McpServer"/>, like <see cref="VirtualKey.AllowedProviders"/>: a key without
/// grants sees no MCP servers, and a global server is granted explicitly too.
/// </summary>
public sealed class VirtualKeyMcpGrant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid VirtualKeyId { get; set; }
    public Guid McpServerId { get; set; }

    /// <summary>Tool names on the server, or <see cref="AllTools"/>. Empty = none.</summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>Used when the gateway runs the server's tools inside an LLM request.</summary>
    public McpApprovalPolicy ApprovalPolicy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public const string AllTools = "*";

    /// <summary>Whether the grant covers the tool named <paramref name="toolName"/> on the server (names are case-sensitive).</summary>
    public bool Allows(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        foreach (var allowed in AllowedTools)
        {
            if (allowed == AllTools || string.Equals(allowed, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class Budget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public BudgetScope Scope { get; set; }
    public Guid ScopeId { get; set; }
    public decimal LimitSek { get; set; }
    public BudgetPeriod Period { get; set; }

    /// <summary>Percentages (1-100) at which alerts are raised.</summary>
    public List<int> AlertThresholds { get; set; } = [50, 80, 100];

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
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
    public int LatencyMs { get; set; }
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

    /// <summary>The MCP server a <see cref="GatewayEndpoint.Mcp"/> or <see cref="GatewayEndpoint.McpToolCall"/> call went to. No foreign key: the server may be deleted later.</summary>
    public Guid? McpServerId { get; set; }

    /// <summary>The MCP method sent to the server, e.g. <c>tools/call</c> or <c>resources/read</c>.</summary>
    public string? McpMethod { get; set; }

    /// <summary>The tool's name on the server, for <c>tools/call</c>.</summary>
    public string? McpTool { get; set; }

    /// <summary>The LLM request inside which the gateway made this call (tool calls and later turns of a gateway-run tool loop).</summary>
    public string? ParentRequestId { get; set; }

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
    public decimal SpentSek { get; set; }
    public decimal LimitSek { get; set; }
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
