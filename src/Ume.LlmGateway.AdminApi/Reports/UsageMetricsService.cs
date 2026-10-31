using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Reports;

/// <summary>The bucket width of <c>/api/usage/metrics</c> (case-insensitive; <c>auto</c> picks hours for up to two days).</summary>
public enum MetricsBucket
{
    Auto,
    Hour,
    Day,
}

/// <summary>The filters of <c>/api/usage/metrics</c>; every one is optional.</summary>
public sealed record UsageMetricsQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Bucket, Guid? DepartmentId, Guid? TeamId, Guid? KeyId,
    string? Model, string? Provider, RequestOutcome? Outcome, GatewayEndpoint? Endpoint);

/// <summary>One bucket (or, with a null start, the whole period) as aggregated in SQL.</summary>
public sealed class UsageMetricsRow
{
    public DateTimeOffset? BucketStart { get; set; }
    public long Requests { get; set; }
    public long Successes { get; set; }
    public long ProviderErrors { get; set; }
    public long Refused { get; set; }
    public long Cancelled { get; set; }
    public long Fallbacks { get; set; }
    public decimal CostUsd { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double? LatencyAvg { get; set; }
    public int? LatencyP50 { get; set; }
    public int? LatencyP90 { get; set; }
    public int? LatencyP95 { get; set; }
    public int? LatencyP99 { get; set; }
    public double? OverheadAvg { get; set; }
    public int? OverheadP50 { get; set; }
    public int? OverheadP90 { get; set; }
    public int? OverheadP95 { get; set; }
    public int? OverheadP99 { get; set; }
    public double? FirstTokenAvg { get; set; }
    public int? FirstTokenP50 { get; set; }
    public int? FirstTokenP95 { get; set; }
    public long ThroughputTokens { get; set; }
    public long ThroughputMs { get; set; }
}

/// <summary>Requests and cost of one requested model in one bucket.</summary>
public sealed class UsageModelRow
{
    public DateTimeOffset BucketStart { get; set; }
    public required string Model { get; set; }
    public long Requests { get; set; }
    public decimal CostUsd { get; set; }
}

/// <summary>
/// The dashboard figures of the Usage page: requests by outcome, cost in USD, tokens, cache hit rate, latency, the
/// gateway's own overhead, time to first token and throughput, per hour or day and for the whole period, aggregated in
/// SQL over the usage records in the user's departments.
/// <para>
/// Monthly totals kept after data retention have no timings and no hours or days, so the dashboard covers only the
/// records: a period reaching back before the oldest kept record starts there (<see cref="UsageMetricsDto.RecordsFrom"/>).
/// </para>
/// <para>
/// Timings leave out live audio sessions (their "latency" is the session's length) and requests no provider answered or
/// failed (refusals take no time worth plotting). Overhead is <c>LatencyMs − UpstreamMs</c>; time spent on failed
/// fallback attempts is provider time. Throughput is output tokens per second of provider time, over successful
/// requests with output. <c>percentile_disc</c> picks an actual value (nearest rank), as the provider statistics do.
/// </para>
/// </summary>
public sealed class UsageMetricsService(AdminContext ctx)
{
    public const int MaxHourBucketDays = 7;
    public const int MaxDayBucketDays = 366;

    /// <summary>Models with their own series; the rest are added up in one.</summary>
    public const int TopModels = 8;

    // Hours are cut in UTC (Stockholm's offset is whole hours, and the autumn DST change must not merge two hours);
    // days follow the municipality's calendar.
    private static readonly string DayZone = BudgetPeriods.Stockholm.Id;

    private static readonly string[] RefusedOutcomes =
        [nameof(RequestOutcome.BudgetExceeded), nameof(RequestOutcome.RateLimited), nameof(RequestOutcome.PiiBlocked), nameof(RequestOutcome.Rejected)];

    private static readonly string[] SessionEndpoints = [nameof(GatewayEndpoint.Realtime), nameof(GatewayEndpoint.RealtimeTranslations)];

    public async Task<UsageMetricsDto> GetAsync(UsageMetricsQuery query, CancellationToken ct)
    {
        var requested = query.Bucket is null ? MetricsBucket.Auto
            : EnumQuery.TryParse<MetricsBucket>(query.Bucket, out var parsed) ? parsed
            : throw new ApiFaultException(400, "The bucket must be auto, hour or day.");
        var (start, end) = ReportQuery.Range(query.From, query.To, ctx.Now);
        var bucket = requested switch
        {
            MetricsBucket.Auto => end - start <= TimeSpan.FromDays(2) ? MetricsBucket.Hour : MetricsBucket.Day,
            _ => requested,
        };
        if (bucket == MetricsBucket.Hour && end - start > TimeSpan.FromDays(MaxHourBucketDays) + TimeSpan.FromHours(1))
        {
            throw new ApiFaultException(400, $"Hourly buckets allow at most {MaxHourBucketDays} days. Choose day for longer periods.");
        }

        // Usage before the oldest kept record is only in monthly totals, which have no timings or days.
        var kept = await ctx.Db.UsageRetainedFromAsync(ct);
        var recordsFrom = kept is { } k && k > start ? (k < end ? k : end) : start;

        var departments = ctx.IsAdmin ? null : await ctx.Departments().Select(d => d.Id).ToArrayAsync(ct);
        var unit = EnumQuery.Name(bucket);
        var zone = bucket == MetricsBucket.Hour ? "UTC" : DayZone;
        // A parameter belongs to one command, so each query gets its own set.
        object[] Parameters() => [.. Filter(query, recordsFrom, end, departments).Parameters, Text("unit", unit), Text("zone", zone)];
        var where = Filter(query, recordsFrom, end, departments).Where;
        var rows = await ctx.Db.Database.SqlQueryRaw<UsageMetricsRow>(MetricsSql(where), Parameters()).ToListAsync(ct);
        var models = await ctx.Db.Database.SqlQueryRaw<UsageModelRow>(ModelsSql(where), Parameters()).ToListAsync(ct);

        var slots = Slots(recordsFrom, end, bucket);
        var byStart = rows.Where(r => r.BucketStart is not null).ToDictionary(r => r.BucketStart!.Value.ToUniversalTime());
        var total = rows.SingleOrDefault(r => r.BucketStart is null) ?? new UsageMetricsRow();
        UsageMetricsBucketDto[] buckets = [.. slots.Select(s => Bucket(s, byStart.GetValueOrDefault(s)))];

        return new UsageMetricsDto(start, end, EnumQuery.Name(bucket), zone, recordsFrom > start ? recordsFrom : null,
            Totals(total), buckets, ModelSeries(models, slots));
    }

    /// <summary>
    /// The WHERE clause and its parameters. Only the filters in use are added, so the planner sees plain conditions
    /// (and the (DepartmentId, Timestamp) index); column names are fixed, values are always parameters.
    /// </summary>
    private static (string Where, List<NpgsqlParameter> Parameters) Filter(UsageMetricsQuery q, DateTimeOffset from, DateTimeOffset to, Guid[]? departments)
    {
        var where = new StringBuilder("""u."Timestamp" >= @from AND u."Timestamp" < @to""");
        List<NpgsqlParameter> parameters =
        [
            new("from", NpgsqlDbType.TimestampTz) { Value = from.UtcDateTime },
            new("to", NpgsqlDbType.TimestampTz) { Value = to.UtcDateTime },
            new("refused", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = RefusedOutcomes },
            new("sessions", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SessionEndpoints },
        ];
        if (departments is not null)
        {
            where.Append(""" AND u."DepartmentId" = ANY(@departments)""");
            parameters.Add(new("departments", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = departments });
        }

        void Add(string column, string name, object? value, NpgsqlDbType type)
        {
            if (value is null)
            {
                return;
            }

            where.Append(CultureInfo.InvariantCulture, $""" AND u."{column}" = @{name}""");
            parameters.Add(new(name, type) { Value = value });
        }

        Add("DepartmentId", "department", q.DepartmentId, NpgsqlDbType.Uuid);
        Add("TeamId", "team", q.TeamId, NpgsqlDbType.Uuid);
        Add("VirtualKeyId", "key", q.KeyId, NpgsqlDbType.Uuid);
        Add("RequestedModel", "model", string.IsNullOrWhiteSpace(q.Model) ? null : q.Model.Trim(), NpgsqlDbType.Text);
        Add("ProviderName", "provider", string.IsNullOrWhiteSpace(q.Provider) ? null : q.Provider.Trim(), NpgsqlDbType.Text);
        Add("Outcome", "outcome", q.Outcome?.ToString(), NpgsqlDbType.Text);
        Add("Endpoint", "endpoint", q.Endpoint?.ToString(), NpgsqlDbType.Text);
        return (where.ToString(), parameters);
    }

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };

    // One scan: GROUPING SETS gives every bucket and, with a null bucket, the whole period (whose percentiles cannot be
    // derived from the buckets').
    private static string MetricsSql(string where) => $"""
        WITH r AS (
            SELECT date_trunc(@unit, u."Timestamp" AT TIME ZONE @zone) AT TIME ZONE @zone AS b,
                   u."Outcome", u."StatusCode", u."FallbackCount", u."CostUsd", u."InputTokens", u."CachedInputTokens", u."OutputTokens",
                   u."UpstreamMs", u."TimeToFirstTokenMs",
                   CASE WHEN u."ProviderAccountId" IS NOT NULL AND NOT (u."Endpoint" = ANY(@sessions)) THEN u."LatencyMs" END AS lat,
                   CASE WHEN u."UpstreamMs" IS NOT NULL AND NOT (u."Endpoint" = ANY(@sessions)) THEN greatest(u."LatencyMs" - u."UpstreamMs", 0) END AS ovh
            FROM "UsageRecords" u
            WHERE {where}
        )
        SELECT b AS "BucketStart",
               count(*) AS "Requests",
               count(*) FILTER (WHERE "Outcome" = 'Success') AS "Successes",
               count(*) FILTER (WHERE "Outcome" = 'ProviderError') AS "ProviderErrors",
               count(*) FILTER (WHERE "Outcome" = ANY(@refused)) AS "Refused",
               count(*) FILTER (WHERE "Outcome" = 'ClientCancelled') AS "Cancelled",
               count(*) FILTER (WHERE "FallbackCount" > 0) AS "Fallbacks",
               coalesce(sum("CostUsd"), 0) AS "CostUsd",
               coalesce(sum("InputTokens"), 0)::bigint AS "InputTokens",
               coalesce(sum("CachedInputTokens"), 0)::bigint AS "CachedInputTokens",
               coalesce(sum("OutputTokens"), 0)::bigint AS "OutputTokens",
               avg(lat)::float8 AS "LatencyAvg",
               percentile_disc(0.5) WITHIN GROUP (ORDER BY lat) AS "LatencyP50",
               percentile_disc(0.9) WITHIN GROUP (ORDER BY lat) AS "LatencyP90",
               percentile_disc(0.95) WITHIN GROUP (ORDER BY lat) AS "LatencyP95",
               percentile_disc(0.99) WITHIN GROUP (ORDER BY lat) AS "LatencyP99",
               avg(ovh)::float8 AS "OverheadAvg",
               percentile_disc(0.5) WITHIN GROUP (ORDER BY ovh) AS "OverheadP50",
               percentile_disc(0.9) WITHIN GROUP (ORDER BY ovh) AS "OverheadP90",
               percentile_disc(0.95) WITHIN GROUP (ORDER BY ovh) AS "OverheadP95",
               percentile_disc(0.99) WITHIN GROUP (ORDER BY ovh) AS "OverheadP99",
               avg("TimeToFirstTokenMs")::float8 AS "FirstTokenAvg",
               percentile_disc(0.5) WITHIN GROUP (ORDER BY "TimeToFirstTokenMs") AS "FirstTokenP50",
               percentile_disc(0.95) WITHIN GROUP (ORDER BY "TimeToFirstTokenMs") AS "FirstTokenP95",
               coalesce(sum("OutputTokens") FILTER (WHERE "Outcome" = 'Success' AND "OutputTokens" > 0 AND "UpstreamMs" > 0), 0)::bigint AS "ThroughputTokens",
               coalesce(sum("UpstreamMs") FILTER (WHERE "Outcome" = 'Success' AND "OutputTokens" > 0 AND "UpstreamMs" > 0), 0)::bigint AS "ThroughputMs"
        FROM r
        GROUP BY GROUPING SETS ((b), ())
        """;

    private static string ModelsSql(string where) => $"""
        SELECT date_trunc(@unit, u."Timestamp" AT TIME ZONE @zone) AT TIME ZONE @zone AS "BucketStart",
               u."RequestedModel" AS "Model",
               count(*) AS "Requests",
               coalesce(sum(u."CostUsd"), 0) AS "CostUsd"
        FROM "UsageRecords" u
        WHERE {where}
        GROUP BY 1, 2
        """;

    /// <summary>Every bucket start in [<paramref name="from"/>, <paramref name="to"/>), so the charts have no gaps.</summary>
    internal static List<DateTimeOffset> Slots(DateTimeOffset from, DateTimeOffset to, MetricsBucket bucket)
    {
        List<DateTimeOffset> slots = [];
        if (bucket == MetricsBucket.Hour)
        {
            var hour = new DateTimeOffset(from.UtcDateTime.Year, from.UtcDateTime.Month, from.UtcDateTime.Day, from.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
            for (; hour < to; hour = hour.AddHours(1))
            {
                slots.Add(hour);
            }

            return slots;
        }

        var zone = BudgetPeriods.Stockholm;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        while (true)
        {
            var local = day.ToDateTime(TimeOnly.MinValue);
            var instant = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            if (instant >= to)
            {
                return slots;
            }

            slots.Add(instant);
            day = day.AddDays(1);
        }
    }

    private static UsageMetricsBucketDto Bucket(DateTimeOffset start, UsageMetricsRow? r) => r is null
        ? new UsageMetricsBucketDto(start, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Empty, Empty, null, null, null)
        : new UsageMetricsBucketDto(start, r.Requests, r.Successes, r.ProviderErrors, r.Refused, r.Cancelled, r.Fallbacks,
            r.CostUsd, r.InputTokens, r.CachedInputTokens, r.OutputTokens,
            new LatencyStatsDto(r.LatencyAvg, r.LatencyP50, r.LatencyP90, r.LatencyP95, r.LatencyP99),
            new LatencyStatsDto(r.OverheadAvg, r.OverheadP50, r.OverheadP90, r.OverheadP95, r.OverheadP99),
            r.FirstTokenAvg, r.FirstTokenP95, TokensPerSecond(r));

    private static readonly LatencyStatsDto Empty = new(null, null, null, null, null);

    private static UsageMetricsTotalsDto Totals(UsageMetricsRow r) => new(
        r.Requests, r.Successes, r.ProviderErrors, r.Refused, r.Cancelled, r.Fallbacks, r.CostUsd, r.InputTokens, r.CachedInputTokens, r.OutputTokens,
        r.InputTokens == 0 ? null : (double)r.CachedInputTokens / r.InputTokens,
        new LatencyStatsDto(r.LatencyAvg, r.LatencyP50, r.LatencyP90, r.LatencyP95, r.LatencyP99),
        new LatencyStatsDto(r.OverheadAvg, r.OverheadP50, r.OverheadP90, r.OverheadP95, r.OverheadP99),
        new FirstTokenStatsDto(r.FirstTokenAvg, r.FirstTokenP50, r.FirstTokenP95),
        TokensPerSecond(r));

    private static double? TokensPerSecond(UsageMetricsRow r) => r.ThroughputMs == 0 ? null : r.ThroughputTokens * 1000d / r.ThroughputMs;

    /// <summary>
    /// The <see cref="TopModels"/> models with the most requests get a series each; the rest one together, with a null
    /// model (so a model that happens to be called "Other" stays its own series).
    /// </summary>
    private static UsageModelSeriesDto[] ModelSeries(List<UsageModelRow> rows, List<DateTimeOffset> slots)
    {
        var index = slots.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        var top = rows.GroupBy(r => r.Model).Select(g => (Model: g.Key, Requests: g.Sum(r => r.Requests), Cost: g.Sum(r => r.CostUsd)))
            .OrderByDescending(m => m.Requests).ThenByDescending(m => m.Cost).ThenBy(m => m.Model, StringComparer.Ordinal)
            .Take(TopModels).Select(m => m.Model).ToHashSet(StringComparer.Ordinal);
        var series = new Dictionary<string, (long[] Requests, decimal[] Cost)>(StringComparer.Ordinal);
        (long[] Requests, decimal[] Cost)? other = null;
        foreach (var row in rows)
        {
            if (!index.TryGetValue(row.BucketStart.ToUniversalTime(), out var i))
            {
                continue;
            }

            (long[] Requests, decimal[] Cost) s;
            if (top.Contains(row.Model))
            {
                if (!series.TryGetValue(row.Model, out s))
                {
                    s = series[row.Model] = (new long[slots.Count], new decimal[slots.Count]);
                }
            }
            else
            {
                s = other ??= (new long[slots.Count], new decimal[slots.Count]);
            }

            s.Requests[i] += row.Requests;
            s.Cost[i] += row.CostUsd;
        }

        IEnumerable<UsageModelSeriesDto> named = series
            .Select(s => new UsageModelSeriesDto(s.Key, s.Value.Requests.Sum(), s.Value.Cost.Sum(), s.Value.Requests, s.Value.Cost))
            .OrderByDescending(s => s.Requests).ThenBy(s => s.Model, StringComparer.Ordinal);
        return other is { } o ? [.. named, new UsageModelSeriesDto(null, o.Requests.Sum(), o.Cost.Sum(), o.Requests, o.Cost)] : [.. named];
    }
}
