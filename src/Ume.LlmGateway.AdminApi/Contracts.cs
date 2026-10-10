using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

// Response bodies of the admin API. The admin UI's types (src/admin-ui/src/api/types.ts) mirror them: property names
// are serialised in camelCase and enums by name, so renaming a property here is a breaking change for the UI.

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);

// ---- organisation and keys -----------------------------------------------------------------------------

public sealed record DepartmentDto(Guid Id, string Name, string CostCenterCode, bool IsActive, int TeamCount, DateTimeOffset CreatedAt)
{
    public static DepartmentDto Of(Department d) => new(d.Id, d.Name, d.CostCenterCode, d.IsActive, d.Teams.Count, d.CreatedAt);
}

public sealed record TeamDto(Guid Id, Guid DepartmentId, string? DepartmentName, string Name, string? Description, bool IsActive, int KeyCount, DateTimeOffset CreatedAt)
{
    public static TeamDto Of(Team t) => new(t.Id, t.DepartmentId, t.Department?.Name, t.Name, t.Description, t.IsActive, t.VirtualKeys.Count, t.CreatedAt);
}

public sealed record KeyDto(
    Guid Id, Guid TeamId, string? TeamName, Guid? DepartmentId, string? DepartmentName, string Name, string? Description, string Prefix,
    KeyStatus Status, bool IsEnabled, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? GraceUntil, DateTimeOffset? LastUsedAt, IReadOnlyList<string> AllowedModels, IReadOnlyList<DataResidency> AllowedResidencies,
    IReadOnlyList<string> AllowedProviders, PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, int? RequestsPerMinute, int? TokensPerMinute,
    Guid? RotatedToKeyId, bool CanReveal)
{
    public static KeyDto Of(VirtualKey k, DateTimeOffset now) => new(
        k.Id, k.TeamId, k.Team?.Name, k.Team?.DepartmentId, k.Team?.Department?.Name, k.Name, k.Description, k.Prefix,
        k.GetStatus(now), k.IsEnabled, k.CreatedAt, k.CreatedBy, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.LastUsedAt,
        k.AllowedModels, k.AllowedResidencies, k.AllowedProviders, k.PiiPolicy, k.AttachmentPolicy, k.RequestsPerMinute, k.TokensPerMinute,
        k.RotatedToKeyId, k.EncryptedSecret is not null);
}

/// <summary>A new key with its secret, which is shown this once.</summary>
public sealed record KeyCreatedDto(KeyDto Key, string Secret);

public sealed record KeyRotatedDto(KeyDto Key, string Secret, KeyDto PreviousKey);

public sealed record KeySecretDto(string Secret);

// ---- providers, models, routes ---------------------------------------------------------------------------

public sealed record ProviderDto(
    Guid Id, string Name, string? DisplayName, ProviderType Type, string BaseUrl, ProviderAuthMode AuthMode, bool HasCredential,
    DataResidency Residency, ProviderCapabilities[] Capabilities, bool IsEnabled, bool IsDrained, int TimeoutSeconds, DateTimeOffset CreatedAt,
    int DeploymentCount)
{
    public static ProviderDto Of(ProviderAccount p) => new(
        p.Id, p.Name, p.DisplayName, p.Type, p.BaseUrl, p.AuthMode, !string.IsNullOrEmpty(p.EncryptedCredential), p.Residency,
        ProviderCatalogService.Capabilities(p.Capabilities), p.IsEnabled, p.IsDrained, p.TimeoutSeconds, p.CreatedAt, p.Deployments.Count);
}

public sealed record PriceDto(
    decimal InputPerMillionUsd, decimal CachedInputPerMillionUsd, decimal OutputPerMillionUsd, decimal AudioPerMinuteUsd,
    decimal AudioInputPerMillionUsd, decimal AudioOutputPerMillionUsd, DateTimeOffset EffectiveFrom)
{
    public static PriceDto Of(ModelPrice p) => new(
        p.InputPerMillionUsd, p.CachedInputPerMillionUsd, p.OutputPerMillionUsd, p.AudioPerMinuteUsd, p.AudioInputPerMillionUsd,
        p.AudioOutputPerMillionUsd, p.EffectiveFrom);
}

public sealed record ModelDto(
    Guid Id, Guid ProviderId, string? ProviderName, DataResidency? Residency, string Name, string UpstreamModel, ModelKind Kind,
    ParameterProfile ParameterProfile, int? ContextWindow, bool IsEnabled, string[] Features, PriceDto? CurrentPrice)
{
    public static ModelDto Of(ModelDeployment m, DateTimeOffset now) => new(
        m.Id, m.ProviderAccountId, m.ProviderAccount?.Name, m.ProviderAccount?.Residency, m.Name, m.UpstreamModel, m.Kind,
        m.ParameterProfile, m.ContextWindow, m.IsEnabled, m.Features, m.PriceAt(now) is { } p ? PriceDto.Of(p) : null);
}

public sealed record RouteDto(Guid Id, string Name, string? Description, ModelKind Kind, bool IsEnabled, RouteTargetDto[] Targets)
{
    public static RouteDto Of(RouteAlias r) => new(
        r.Id, r.Name, r.Description, r.Kind, r.IsEnabled,
        [.. r.Targets.OrderBy(t => t.Priority).Select(t => new RouteTargetDto(
            t.ModelDeploymentId, t.ModelDeployment?.Name, t.ModelDeployment?.ProviderAccount?.Name, t.ModelDeployment?.ProviderAccount?.Residency,
            t.Priority, t.Weight))]);
}

public sealed record RouteTargetDto(Guid ModelId, string? ModelName, string? ProviderName, DataResidency? Residency, int Priority, int Weight);

public sealed record ExchangeRateDto(string Currency, decimal SekPerUnit, DateTimeOffset EffectiveFrom)
{
    public static ExchangeRateDto Of(ExchangeRate r) => new(r.Currency, r.SekPerUnit, r.EffectiveFrom);
}

// ---- budgets ---------------------------------------------------------------------------------------------

public sealed record BudgetDto(
    Guid Id, BudgetScope Scope, Guid ScopeId, string ScopeName, decimal LimitSek, BudgetPeriod Period, IReadOnlyList<int> AlertThresholds,
    bool IsActive, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, decimal SpentSek, decimal PercentUsed);

public sealed record AlertDto(
    Guid Id, Guid BudgetId, BudgetScope Scope, string ScopeName, int ThresholdPercent, decimal SpentSek, decimal LimitSek,
    DateTimeOffset PeriodStart, DateTimeOffset Timestamp, bool Acknowledged);

// ---- routing rules ---------------------------------------------------------------------------------------

/// <summary>
/// <c>IsOrphaned</c>: the key, team or department the rule is scoped to no longer exists. Such a rule never applies
/// (and cannot be enabled) until it is reassigned. <c>ValidationErrors</c> lists why the gateway ignores an enabled rule.
/// </summary>
public sealed record RuleView(
    Guid Id, string Name, string? Description, bool IsEnabled, int Priority, string Scope, Guid? ScopeId, string? ScopeName,
    bool IsOrphaned, string Condition, bool Chain, IReadOnlyList<RuleTargetView> Targets, IReadOnlyList<string> Fallbacks,
    IReadOnlyList<RuleProblem> ValidationErrors, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RuleTargetView(string Model, int Weight);

public sealed record RuleProblem(string Message, int? Position, int? Length);

public sealed record ConditionCheckResult(bool Valid, IReadOnlyList<ConditionError> Errors, IReadOnlyList<string> Variables, IReadOnlyList<VariableView> Available);

public sealed record ConditionError(int Position, int Length, string Message)
{
    public static ConditionError Of(ExpressionError e) => new(e.Position, e.Length, e.Message);
}

public sealed record VariableView(string Name, string Type);

public sealed record RuleTestResult(
    bool Matched, string? PrimaryModel, IReadOnlyList<ModelInfoView> Models, IReadOnlyList<AppliedRuleView> Applied, bool ChainLimitReached,
    IReadOnlyList<RuleEvaluationView> Evaluation, IReadOnlyList<IgnoredRuleView> IgnoredRules, string Note);

public sealed record ModelInfoView(string Name, bool Exists, string Type, string? Kind, bool Enabled);

public sealed record AppliedRuleView(Guid RuleId, string Name, string FromModel, string ToModel);

public sealed record RuleEvaluationView(Guid RuleId, string Name, string Scope, int Priority, int ChainStep, string Model, string Outcome, IReadOnlyList<TraceView> Trace)
{
    public static RuleEvaluationView Of(RuleEvaluation e) => new(
        e.RuleId, e.Name, e.Scope.ToString(), e.Priority, e.ChainStep, e.Model, e.Outcome.ToString(),
        [.. e.Trace.Select(t => new TraceView(t.Text, t.LeftValue, t.Result))]);
}

public sealed record TraceView(string Text, string? LeftValue, bool? Result);

public sealed record IgnoredRuleView(Guid RuleId, string RuleName, string Message, int? Position, int? Length)
{
    public static IgnoredRuleView Of(RuleBuildError e) => new(e.RuleId, e.RuleName, e.Message, e.Expression?.Position, e.Expression?.Length);
}

// ---- reports and operations ------------------------------------------------------------------------------

public sealed record UsageRequestDto(
    string RequestId, DateTimeOffset Timestamp, string KeyPrefix, string KeyName, string TeamName, string DepartmentName, string Endpoint,
    string RequestedModel, string? ProviderName, string? UpstreamModel, long InputTokens, long CachedInputTokens, long OutputTokens,
    decimal CostSek, int LatencyMs, int StatusCode, RequestOutcome Outcome, int FallbackCount, bool Streamed, PiiPolicy? PiiActionApplied,
    string? PiiCategories, string? ErrorCode, Guid? RoutingRuleId, string? RoutingRuleName);

public sealed record UsageSummaryDto(
    DateTimeOffset From, DateTimeOffset To, decimal TotalCostSek, long TotalRequests, long TotalInputTokens, long TotalOutputTokens,
    long TotalErrors, IReadOnlyList<UsageSummaryRow> Rows);

public sealed record AuditEntryDto(long Id, DateTimeOffset Timestamp, string Actor, string Action, string EntityType, string? EntityId, string? Details);

public sealed record CatalogDto(string GatewayBaseUrl, IReadOnlyList<CatalogModelDto> Models, IReadOnlyList<CatalogRouteDto> Routes);

public sealed record CatalogModelDto(
    string Name, ModelKind Kind, DataResidency Residency, ProviderCapabilities[] Capabilities, decimal? InputSekPerMillion, decimal? OutputSekPerMillion);

public sealed record CatalogRouteDto(
    string Name, string? Description, ModelKind Kind, DataResidency[] Residencies, ProviderCapabilities[] Capabilities,
    decimal? InputSekPerMillion, decimal? OutputSekPerMillion);

public sealed record OpsHealthDto(
    DateTimeOffset CheckedAt, IReadOnlyList<ComponentHealthDto> Components, VersionsDto Versions, UsageWriterStatus? UsageWriter,
    IReadOnlyList<ProviderHealthDto> Providers);

public sealed record ComponentHealthDto(string Name, string Status, string Description);

public sealed record VersionsDto(string? AdminApi, string? Gateway, string Schema);

public sealed record ProviderHealthDto(
    Guid Id, string Name, ProviderType Type, DataResidency Residency, bool IsEnabled, bool IsDrained, CircuitState CircuitState,
    long Requests24h, double ErrorRate24h, double FallbackRate24h, int? P50LatencyMs, int? P95LatencyMs);

public sealed record ConfigImportResult(int Created, int Updated, int Skipped);
