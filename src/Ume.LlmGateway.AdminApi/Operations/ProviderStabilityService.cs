using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Operations;

/// <summary>What the admin UI shows for a provider (computed here so the UI never re-implements the thresholds).</summary>
public enum ProviderStatus
{
    Healthy,
    Degraded,
    Unhealthy,
    CircuitOpen,
    NoRecentTraffic,
    Disabled,
}

/// <summary>The width of a time-series bucket in <c>/api/ops/providers/stats</c> (case-insensitive).</summary>
public enum StatsBucket
{
    Hour,
    Day,
}

/// <summary>A provider's requests in a period, aggregated in the database.</summary>
public sealed class ProviderUsageStats
{
    public Guid ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public long Requests { get; set; }

    /// <summary>Every request that did not succeed (client errors and cancellations included).</summary>
    public long Errors { get; set; }

    /// <summary>Failures the provider is responsible for; see <see cref="ProviderStabilityService"/>.</summary>
    public long ProviderErrors { get; set; }

    public long Fallbacks { get; set; }
    public int? P50FirstTokenMs { get; set; }
    public int? P95FirstTokenMs { get; set; }
    public long RecentRequests { get; set; }
    public long RecentProviderErrors { get; set; }
}

/// <summary>One provider's figures in one time bucket.</summary>
public sealed class ProviderBucketStats
{
    public Guid ProviderId { get; set; }
    public DateTimeOffset BucketStart { get; set; }
    public long Requests { get; set; }
    public long ProviderErrors { get; set; }
    public long Fallbacks { get; set; }
    public int? P50FirstTokenMs { get; set; }
    public int? P95FirstTokenMs { get; set; }
}

/// <summary>
/// Provider status and stability figures from the usage records. The gateway writes one record per request (not per
/// attempt), attributed to the provider that answered or failed last.
/// <para>
/// Speed is the <b>provider's time to first token</b> (<c>ProviderFirstTokenMs</c>): on streamed answers, from sending the
/// request to the provider that answered until its first event. A request's end-to-end <c>LatencyMs</c> would not do: it
/// grows with the length of the answer and includes failed fallback attempts on other providers and the gateway's own
/// time. Answers that are not streamed only arrive once complete, so they have no comparable figure and are left out.
/// </para>
/// <para>
/// A <b>provider error</b> is a request with outcome <c>ProviderError</c> and a 5xx status: the provider failed, timed out
/// or broke off a stream. Client errors the provider rejected (4xx, outcome <c>ProviderError</c> too), cancellations,
/// rejections by the gateway (budget, rate limit, PII) and the gateway's own internal errors are not held against it.
/// </para>
/// </summary>
public sealed class ProviderStabilityService(GatewayDbContext db)
{
    /// <summary>The recent window the status is computed over.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    /// <summary>Fewer requests than this in <see cref="RecentWindow"/> are too few to call a provider degraded.</summary>
    public const int MinimumRecentRequests = 10;

    public const double DegradedErrorRate = 0.05;
    public const double UnhealthyErrorRate = 0.5;
    public const int MaxHourBucketDays = 7;
    public const int MaxDayBucketDays = 90;

    /// <summary>Day buckets follow the municipality's calendar; hour buckets are the same in UTC (whole-hour offsets).</summary>
    public static readonly string TimeZone = BudgetPeriods.Stockholm.Id;

    // Written by the gateway's GatewayErrorCodes.InternalError: an exception in the gateway, not the provider's fault.
    private const string InternalErrorCode = "internal_error";
    private static readonly string ProviderError = nameof(RequestOutcome.ProviderError);
    private static readonly string Success = nameof(RequestOutcome.Success);

    /// <summary>
    /// Requests, errors, provider errors, fallbacks and time-to-first-token percentiles per provider in [<paramref name="from"/>,
    /// <paramref name="to"/>), plus requests and provider errors since <paramref name="recentSince"/>, aggregated in SQL.
    /// Only streamed answers have a time to first token (live audio sessions and answers that were not streamed have
    /// none); <c>percentile_disc</c> skips the others and picks the smallest value at or above the percentile, as the
    /// nearest-rank method does.
    /// </summary>
    public Task<List<ProviderUsageStats>> PeriodAsync(DateTimeOffset from, DateTimeOffset to, DateTimeOffset recentSince, CancellationToken ct) =>
        db.Database.SqlQuery<ProviderUsageStats>($"""
            SELECT "ProviderAccountId" AS "ProviderId",
                   max("ProviderName") AS "ProviderName",
                   count(*) AS "Requests",
                   count(*) FILTER (WHERE "Outcome" <> {Success}) AS "Errors",
                   count(*) FILTER (WHERE "Outcome" = {ProviderError} AND "StatusCode" >= 500 AND "ErrorCode" IS DISTINCT FROM {InternalErrorCode}) AS "ProviderErrors",
                   count(*) FILTER (WHERE "FallbackCount" > 0) AS "Fallbacks",
                   percentile_disc(0.5) WITHIN GROUP (ORDER BY "ProviderFirstTokenMs") AS "P50FirstTokenMs",
                   percentile_disc(0.95) WITHIN GROUP (ORDER BY "ProviderFirstTokenMs") AS "P95FirstTokenMs",
                   count(*) FILTER (WHERE "Timestamp" >= {recentSince}) AS "RecentRequests",
                   count(*) FILTER (WHERE "Timestamp" >= {recentSince} AND "Outcome" = {ProviderError} AND "StatusCode" >= 500 AND "ErrorCode" IS DISTINCT FROM {InternalErrorCode}) AS "RecentProviderErrors"
            FROM "UsageRecords"
            WHERE "Timestamp" >= {from} AND "Timestamp" < {to} AND "ProviderAccountId" IS NOT NULL
            GROUP BY "ProviderAccountId"
            """).ToListAsync(ct);

    /// <summary>The same figures per provider and bucket (only buckets with traffic), bucketed in SQL.</summary>
    public Task<List<ProviderBucketStats>> BucketsAsync(DateTimeOffset from, DateTimeOffset to, StatsBucket bucket, CancellationToken ct)
    {
        // date_trunc's field name and the zone are parameters, never spliced into the SQL. Hours are cut in UTC so the
        // autumn DST change does not merge two hours into one bucket; Stockholm's offset is whole hours, so they match.
        var unit = EnumQuery.Name(bucket);
        var zone = bucket == StatsBucket.Hour ? "UTC" : TimeZone;
        return db.Database.SqlQuery<ProviderBucketStats>($"""
            SELECT "ProviderAccountId" AS "ProviderId",
                   date_trunc({unit}, "Timestamp" AT TIME ZONE {zone}) AT TIME ZONE {zone} AS "BucketStart",
                   count(*) AS "Requests",
                   count(*) FILTER (WHERE "Outcome" = {ProviderError} AND "StatusCode" >= 500 AND "ErrorCode" IS DISTINCT FROM {InternalErrorCode}) AS "ProviderErrors",
                   count(*) FILTER (WHERE "FallbackCount" > 0) AS "Fallbacks",
                   percentile_disc(0.5) WITHIN GROUP (ORDER BY "ProviderFirstTokenMs") AS "P50FirstTokenMs",
                   percentile_disc(0.95) WITHIN GROUP (ORDER BY "ProviderFirstTokenMs") AS "P95FirstTokenMs"
            FROM "UsageRecords"
            WHERE "Timestamp" >= {from} AND "Timestamp" < {to} AND "ProviderAccountId" IS NOT NULL
            GROUP BY 1, 2
            ORDER BY 1, 2
            """).ToListAsync(ct);
    }

    /// <summary>Time series and a ranking (most stable first) for <c>GET /api/ops/providers/stats</c>.</summary>
    public async Task<ProviderStabilityDto> StabilityAsync(DateTimeOffset? from, DateTimeOffset? to, string? bucket, DateTimeOffset now, CancellationToken ct)
    {
        var size = bucket is null ? StatsBucket.Hour
            : EnumQuery.TryParse<StatsBucket>(bucket, out var parsed) ? parsed
            : throw new ApiFaultException(400, "The bucket must be hour or day.");
        var end = to?.ToUniversalTime() ?? now;
        var start = BucketStart(from?.ToUniversalTime() ?? end - (size == StatsBucket.Hour ? TimeSpan.FromDays(1) : TimeSpan.FromDays(30)), size);
        var maxDays = size == StatsBucket.Hour ? MaxHourBucketDays : MaxDayBucketDays;
        if (start >= end)
        {
            throw new ApiFaultException(400, "The end of the time range must be after its start.");
        }

        if (end - start > TimeSpan.FromDays(maxDays) + TimeSpan.FromHours(size == StatsBucket.Hour ? 1 : 25))
        {
            throw new ApiFaultException(400, size == StatsBucket.Hour
                ? $"Hourly buckets allow at most {MaxHourBucketDays} days. Choose day for longer periods."
                : $"Choose a time range of at most {MaxDayBucketDays} days.");
        }

        var totals = await PeriodAsync(start, end, end, ct);
        var buckets = await BucketsAsync(start, end, size, ct);
        var names = await db.ProviderAccounts.AsNoTracking().Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        ProviderRankDto[] ranking =
        [
            .. totals.Select(s => new ProviderRankDto(
                    s.ProviderId, names.GetValueOrDefault(s.ProviderId) ?? s.ProviderName ?? "Deleted provider", names.ContainsKey(s.ProviderId),
                    s.Requests, s.ProviderErrors, 1 - Rate(s.ProviderErrors, s.Requests), Rate(s.Fallbacks, s.Requests), s.P50FirstTokenMs, s.P95FirstTokenMs))
                .OrderByDescending(r => r.Availability)
                .ThenBy(r => r.P95FirstTokenMs ?? int.MaxValue)
                .ThenByDescending(r => r.Requests)
                .ThenBy(r => r.Name, StringComparer.Ordinal),
        ];
        return new ProviderStabilityDto(start, end, size, size == StatsBucket.Hour ? "UTC" : TimeZone, ranking,
            [.. buckets.Select(b => new ProviderBucketDto(b.ProviderId, b.BucketStart.ToUniversalTime(), b.Requests, b.ProviderErrors,
                Rate(b.ProviderErrors, b.Requests), b.Fallbacks, b.P50FirstTokenMs, b.P95FirstTokenMs))]);
    }

    /// <summary>
    /// The status shown for a provider: disabled and an open circuit win; otherwise the share of provider errors in the
    /// last <see cref="RecentWindow"/>, once there are at least <see cref="MinimumRecentRequests"/> requests.
    /// </summary>
    public static (ProviderStatus Status, string Reason) Classify(bool isEnabled, CircuitState circuit, long recentRequests, long recentProviderErrors)
    {
        if (!isEnabled)
        {
            return (ProviderStatus.Disabled, "Disabled: the gateway sends it no requests.");
        }

        if (circuit == CircuitState.Open)
        {
            return (ProviderStatus.CircuitOpen, "Circuit open: the gateway tries other providers first until the circuit closes.");
        }

        if (recentRequests == 0)
        {
            return (ProviderStatus.NoRecentTraffic, "No requests in the last hour.");
        }

        var failed = string.Create(CultureInfo.InvariantCulture,
            $"{recentProviderErrors} of {recentRequests} {(recentRequests == 1 ? "request" : "requests")} failed at the provider in the last hour");
        if (recentRequests < MinimumRecentRequests)
        {
            return (ProviderStatus.Healthy, string.Create(CultureInfo.InvariantCulture,
                $"{failed}; fewer than {MinimumRecentRequests} requests are too few to judge, so only the circuit counts."));
        }

        var rate = recentProviderErrors / (double)recentRequests;
        var text = string.Create(CultureInfo.InvariantCulture, $"{failed} ({rate * 100:0.#} %).");
        return rate >= UnhealthyErrorRate ? (ProviderStatus.Unhealthy, text)
            : rate >= DegradedErrorRate ? (ProviderStatus.Degraded, text)
            : (ProviderStatus.Healthy, text);
    }

    private static double Rate(long part, long whole) => whole == 0 ? 0 : part / (double)whole;

    /// <summary>The start of the bucket containing <paramref name="at"/>: the whole hour, or Stockholm midnight.</summary>
    private static DateTimeOffset BucketStart(DateTimeOffset at, StatsBucket bucket)
    {
        if (bucket == StatsBucket.Hour)
        {
            return new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, 0, 0, TimeSpan.Zero);
        }

        var local = TimeZoneInfo.ConvertTime(at, BudgetPeriods.Stockholm).Date;
        return new DateTimeOffset(local, BudgetPeriods.Stockholm.GetUtcOffset(local)).ToUniversalTime();
    }
}
