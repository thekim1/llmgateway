using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.AdminApi.Settings;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record NetworkSettingsRequest(
    [property: Required, MaxLength(NetworkRanges.MaxEntries)] List<string> TrustedProxies) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TrustedProxies is null)
        {
            yield break;
        }

        if (NetworkRanges.Normalize(TrustedProxies, out var canonical) is { } error)
        {
            yield return new ValidationResult(error, [nameof(TrustedProxies)]);
        }
        else if (canonical.Any(e => NetworkRanges.TryParse(e, out var n, out _) && NetworkRanges.IsEverything(n)))
        {
            // Every caller could then claim any address in X-Forwarded-For and walk past key address restrictions.
            yield return new ValidationResult("A network that covers every address (/0) cannot be a trusted proxy. Enter the proxy's own addresses.", [nameof(TrustedProxies)]);
        }
    }
}

public sealed record RetentionSettingsRequest(
    [property: Range(RetentionPolicy.MinimumDays, RetentionPolicy.MaximumDays)] int UsageRetentionDays,
    [property: Range(RetentionPolicy.MinimumDays, RetentionPolicy.MaximumDays)] int? AuditRetentionDays) : AdminRequest;

/// <summary>
/// Retention settings and job state. <c>UsageRetainedFrom</c>: the oldest instant usage records cover (before it only monthly
/// totals exist; null until the first removal). <c>NextUsageCutoff</c>: usage, refused-credential and alert rows before it go
/// at the next run with today's settings. <c>NextAuditCutoff</c>: the same for audit entries; null when they are kept for ever.
/// </summary>
public sealed record RetentionSettingsDto(
    int UsageRetentionDays, int? AuditRetentionDays, DateTimeOffset? UsageRetainedFrom, DateTimeOffset NextUsageCutoff, DateTimeOffset? NextAuditCutoff,
    bool Enabled, DateTimeOffset? NextRunAt, bool Running, DateTimeOffset? LastRunAt, long? LastRunDeleted, string? LastRunError);

/// <summary>
/// The connection the admin API saw for this request, to help find the proxy's address: behind the same proxy as the
/// gateway, <paramref name="RemoteAddress"/> is the proxy and <paramref name="ForwardedFor"/> what it added.
/// </summary>
public sealed record ObservedConnectionDto(string? RemoteAddress, string? ForwardedFor);

public sealed record NetworkSettingsDto(IReadOnlyList<string> TrustedProxies, DateTimeOffset? UpdatedAt, ObservedConnectionDto Observed);

/// <summary>
/// Turns logging of every request on or off. <c>NoTimeLimit</c>: when switched on, it stays on until it is turned off;
/// otherwise it switches itself off after <see cref="GatewaySettingsService.RequestLogDuration"/>. Ignored when switching off.
/// </summary>
public sealed record RequestLogSettingsRequest(bool LogAllRequests, bool NoTimeLimit) : AdminRequest;

/// <summary>
/// Whether the gateways log every request. <c>Active</c>: logging is on right now (switched on and not expired).
/// <c>Until</c>: when it switches itself off; null with <c>Active</c> = no time limit.
/// </summary>
public sealed record RequestLogSettingsDto(bool Active, DateTimeOffset? Until, DateTimeOffset? UpdatedAt);
