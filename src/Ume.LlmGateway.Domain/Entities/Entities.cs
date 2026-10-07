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

    public PiiPolicy PiiPolicy { get; set; } = PiiPolicy.Off;
    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }

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

    public bool Supports(GatewayEndpoint endpoint) => endpoint switch
    {
        GatewayEndpoint.ChatCompletions => Capabilities.HasFlag(ProviderCapabilities.ChatCompletions),
        GatewayEndpoint.Embeddings => Capabilities.HasFlag(ProviderCapabilities.Embeddings),
        GatewayEndpoint.Responses => Capabilities.HasFlag(ProviderCapabilities.Responses),
        GatewayEndpoint.AnthropicMessages => Capabilities.HasFlag(ProviderCapabilities.AnthropicMessages),
        _ => false,
    };
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
    public bool IsEnabled { get; set; } = true;
    public List<ModelPrice> Prices { get; set; } = [];

    public ModelPrice? PriceAt(DateTimeOffset when) =>
        Prices.Where(p => p.EffectiveFrom <= when).MaxBy(p => p.EffectiveFrom);
}

/// <summary>Price per one million tokens in USD, valid from <see cref="EffectiveFrom"/>.</summary>
public sealed class ModelPrice
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ModelDeploymentId { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public decimal InputPerMillionUsd { get; set; }
    public decimal CachedInputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }
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
