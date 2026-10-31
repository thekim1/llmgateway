using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Operations;

// Request and response bodies. The admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses: property
// names are serialised in camelCase and enums by name, so renaming a property is a breaking change for the UI.

public sealed record CircuitRequest([property: EnumDataType(typeof(CircuitState))] CircuitState State) : AdminRequest;

public sealed record OpsHealthDto(
    DateTimeOffset CheckedAt, IReadOnlyList<ComponentHealthDto> Components, VersionsDto Versions, UsageWriterStatus? UsageWriter,
    IReadOnlyList<ProviderHealthDto> Providers);

public sealed record ComponentHealthDto(string Name, string Status, string Description);

public sealed record VersionsDto(string? AdminApi, string? Gateway, string Schema);

/// <summary>
/// A provider's status (from the last hour, see <see cref="ProviderStabilityService.Classify"/>) and its last 24 hours.
/// <c>ErrorRate24h</c> counts every request that did not succeed; <c>ProviderErrorRate24h</c> only the provider's own failures.
/// <c>P50FirstTokenMs</c>/<c>P95FirstTokenMs</c> are the provider's time to first token on streamed answers, without
/// fallbacks or gateway time (see <see cref="ProviderStabilityService"/>); null when it streamed no answers.
/// </summary>
public sealed record ProviderHealthDto(
    Guid Id, string Name, ProviderType Type, DataResidency Residency, bool IsEnabled, bool IsDrained, CircuitState CircuitState,
    long Requests24h, double ErrorRate24h, double FallbackRate24h, int? P50FirstTokenMs, int? P95FirstTokenMs,
    double ProviderErrorRate24h, ProviderStatus Status, string StatusReason, long RecentRequests, long RecentProviderErrors);

/// <summary>Provider figures per bucket and a ranking over the whole period (<c>GET /api/ops/providers/stats</c>).</summary>
public sealed record ProviderStabilityDto(
    DateTimeOffset From, DateTimeOffset To, StatsBucket Bucket, string TimeZone, IReadOnlyList<ProviderRankDto> Providers, IReadOnlyList<ProviderBucketDto> Buckets);

/// <summary>A provider over the whole period; <c>Availability</c> is 1 minus its provider error rate.</summary>
public sealed record ProviderRankDto(
    Guid ProviderId, string Name, bool Exists, long Requests, long ProviderErrors, double Availability, double FallbackRate, int? P50FirstTokenMs, int? P95FirstTokenMs);

/// <summary>One provider in one bucket that had traffic (buckets without traffic are left out).</summary>
public sealed record ProviderBucketDto(
    Guid ProviderId, DateTimeOffset Start, long Requests, long ProviderErrors, double ErrorRate, long Fallbacks, int? P50FirstTokenMs, int? P95FirstTokenMs);
