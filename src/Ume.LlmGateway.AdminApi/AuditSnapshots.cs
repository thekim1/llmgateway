using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Audit log <c>Action</c> values.</summary>
public static class AuditActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Drain = "drain";
    public const string Price = "price";
    public const string Rotate = "rotate";
    public const string Revoke = "revoke";
    public const string Reveal = "reveal";
    public const string Copy = "copy";
    public const string Reassign = "reassign";
    public const string Reorder = "reorder";
    public const string Deactivate = "deactivate";
    public const string Acknowledge = "acknowledge";
    public const string Circuit = "circuit";
    public const string Import = "import";
    public const string Invalidate = "invalidate";
}

/// <summary>Audit log <c>EntityType</c> values (also the <c>entityType</c> filter of <c>GET /api/audit</c>).</summary>
public static class AuditEntities
{
    public const string Department = "Department";
    public const string Team = "Team";
    public const string VirtualKey = "VirtualKey";
    public const string Provider = "Provider";
    public const string Model = "Model";
    public const string Route = "Route";
    public const string RoutingRule = "RoutingRule";
    public const string Budget = "Budget";
    public const string Alert = "Alert";
    public const string ExchangeRate = "ExchangeRate";
    public const string Config = "Config";
    public const string Keys = "Keys";
}

/// <summary>The <c>Details</c> of an audit entry.</summary>
public sealed record AuditDetails(object? Before, object? After);

internal static class AuditJson
{
    /// <summary>camelCase and enum names, like the API's own responses.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

// Audit snapshots: what an entity's stored settings were before and after a change. They hold only the entity's own
// columns, so an entry does not depend on which navigations were loaded or on the clock, and never a secret.

public sealed record DepartmentAudit(Guid Id, string Name, string CostCenterCode, bool IsActive)
{
    public static DepartmentAudit Of(Department d) => new(d.Id, d.Name, d.CostCenterCode, d.IsActive);
}

public sealed record TeamAudit(Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive)
{
    public static TeamAudit Of(Team t) => new(t.Id, t.DepartmentId, t.Name, t.Description, t.IsActive);
}

public sealed record KeyAudit(
    Guid Id, Guid TeamId, string Name, string? Description, string Prefix, bool IsEnabled, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? GraceUntil, Guid? RotatedToKeyId, IReadOnlyList<string> AllowedModels, IReadOnlyList<DataResidency> AllowedResidencies,
    IReadOnlyList<string> AllowedProviders, PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, int? RequestsPerMinute, int? TokensPerMinute)
{
    public static KeyAudit Of(VirtualKey k) => new(
        k.Id, k.TeamId, k.Name, k.Description, k.Prefix, k.IsEnabled, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.RotatedToKeyId,
        [.. k.AllowedModels], [.. k.AllowedResidencies], [.. k.AllowedProviders], k.PiiPolicy, k.AttachmentPolicy, k.RequestsPerMinute, k.TokensPerMinute);
}

public sealed record KeyRotationAudit(KeyAudit PreviousKey, KeyAudit Key);

/// <summary>A reveal or copy of a key's secret: which key, never the secret.</summary>
public sealed record KeyAccessAudit(string Name, string Prefix);

public sealed record ProviderAudit(
    Guid Id, string Name, string? DisplayName, ProviderType Type, string BaseUrl, ProviderAuthMode AuthMode, bool HasCredential,
    DataResidency Residency, ProviderCapabilities[] Capabilities, bool IsEnabled, bool IsDrained, int TimeoutSeconds)
{
    public static ProviderAudit Of(ProviderAccount p) => new(
        p.Id, p.Name, p.DisplayName, p.Type, p.BaseUrl, p.AuthMode, !string.IsNullOrEmpty(p.EncryptedCredential), p.Residency,
        ProviderCatalogService.Capabilities(p.Capabilities), p.IsEnabled, p.IsDrained, p.TimeoutSeconds);
}

public sealed record CircuitAudit(CircuitState State);

public sealed record ModelAudit(
    Guid Id, Guid ProviderId, string Name, string UpstreamModel, ModelKind Kind, ParameterProfile ParameterProfile, int? ContextWindow,
    bool IsEnabled, IReadOnlyList<string> Features)
{
    public static ModelAudit Of(ModelDeployment m) => new(
        m.Id, m.ProviderAccountId, m.Name, m.UpstreamModel, m.Kind, m.ParameterProfile, m.ContextWindow, m.IsEnabled, [.. m.Features]);
}

public sealed record RouteAudit(Guid Id, string Name, string? Description, ModelKind Kind, bool IsEnabled, IReadOnlyList<RouteTargetAudit> Targets)
{
    public static RouteAudit Of(RouteAlias r) => new(
        r.Id, r.Name, r.Description, r.Kind, r.IsEnabled,
        [.. r.Targets.OrderBy(t => t.Priority).ThenBy(t => t.ModelDeploymentId).Select(t => new RouteTargetAudit(t.ModelDeploymentId, t.Priority, t.Weight))]);
}

public sealed record RouteTargetAudit(Guid ModelId, int Priority, int Weight);

public sealed record RoutingRuleAudit(
    Guid Id, string Name, bool IsEnabled, int Priority, RoutingScope Scope, Guid? ScopeId, string Condition, bool Chain,
    IReadOnlyList<RuleTargetView> Targets, IReadOnlyList<string> Fallbacks)
{
    public static RoutingRuleAudit Of(RoutingRule r) => new(
        r.Id, r.Name, r.IsEnabled, r.Priority, r.Scope, r.ScopeId, r.Condition, r.Chain,
        [.. r.Targets.Select(t => new RuleTargetView(t.Model, t.Weight))], [.. r.Fallbacks]);
}

public sealed record RulePriorityAudit(Guid Id, int Priority);

/// <summary>Why a rule was deleted or deactivated along with its team or department.</summary>
public sealed record RuleScopeRemovalAudit(string Reason, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled = null);

public sealed record BudgetAudit(BudgetScope Scope, Guid ScopeId, decimal LimitSek, BudgetPeriod Period, IReadOnlyList<int> AlertThresholds, bool IsActive)
{
    public static BudgetAudit Of(Budget b) => new(b.Scope, b.ScopeId, b.LimitSek, b.Period, [.. b.AlertThresholds], b.IsActive);
}

public sealed record AlertAudit(bool Acknowledged);
