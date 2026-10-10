using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.ServiceDefaults;

namespace Ume.LlmGateway.DataApi;

/// <summary>A row of an incremental feed: ordered by <see cref="Id"/>, handed out once <see cref="RecordedAt"/> has settled.</summary>
public interface IFeedItem
{
    long Id { get; }
    DateTimeOffset RecordedAt { get; }
}

/// <summary>One page of a feed. Pass <see cref="NextCursor"/> as <c>after</c> to continue; it never moves backwards.</summary>
public sealed record FeedPage<T>(IReadOnlyList<T> Items, string NextCursor, bool HasMore);

public static class Feeds
{
    /// <summary>Parses <c>after</c> and <c>limit</c>; the caller queries <c>Id &gt; after ORDER BY Id</c> with <c>Take(limit + 1)</c>.</summary>
    public static (long After, int Limit) Window(string? after, int? limit, DataApiOptions options)
    {
        long cursor = 0;
        if (!string.IsNullOrEmpty(after) && (!long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out cursor) || cursor < 0))
        {
            throw new ApiFaultException(StatusCodes.Status400BadRequest, "'after' must be a cursor returned by an earlier page (or omitted to start from the beginning).");
        }

        var size = limit ?? Math.Min(1000, options.MaxPageSize);
        if (size < 1 || size > options.MaxPageSize)
        {
            throw new ApiFaultException(StatusCodes.Status400BadRequest, $"'limit' must be between 1 and {options.MaxPageSize}.");
        }

        return (cursor, size);
    }

    /// <summary>
    /// Builds the page from up to <c>limit + 1</c> rows in id order. Stops at the first row that has not settled: rows
    /// with a lower id may still be committing, and a cursor must never pass a row the consumer has not seen.
    /// </summary>
    public static FeedPage<T> Page<T>(IReadOnlyList<T> rows, long after, int limit, DateTimeOffset cutoff) where T : IFeedItem
    {
        var items = new List<T>(Math.Min(rows.Count, limit));
        var settledToEnd = true;
        foreach (var row in rows)
        {
            if (row.RecordedAt >= cutoff)
            {
                settledToEnd = false;
                break;
            }

            if (items.Count == limit)
            {
                break;
            }

            items.Add(row);
        }

        var hasMore = settledToEnd && rows.Count > limit;
        var next = items.Count > 0 ? items[^1].Id : after;
        return new FeedPage<T>(items, next.ToString(CultureInfo.InvariantCulture), hasMore);
    }

    public static DateTimeOffset Cutoff(TimeProvider time, DataApiOptions options) =>
        time.GetUtcNow().AddSeconds(-options.SettleSeconds);
}

/// <summary>
/// Writes results as JSON (default), NDJSON or CSV, chosen by <c>?format=</c> or the Accept header, so integration
/// tools that cannot set headers can still ask for CSV. Feed cursors also go in <c>X-Next-Cursor</c>/<c>X-Has-More</c>.
/// </summary>
public static class DataResults
{
    public static IResult Feed<T>(HttpContext http, FeedPage<T> page)
    {
        http.Response.Headers["X-Next-Cursor"] = page.NextCursor;
        http.Response.Headers["X-Has-More"] = page.HasMore ? "true" : "false";
        return Format(http) switch
        {
            "csv" => Csv(http, page.Items),
            "ndjson" => NdJson(http, page.Items),
            _ => Results.Json(new { items = page.Items, nextCursor = page.NextCursor, hasMore = page.HasMore }, Json(http)),
        };
    }

    public static IResult List<T>(HttpContext http, IReadOnlyList<T> items) => Format(http) switch
    {
        "csv" => Csv(http, items),
        "ndjson" => NdJson(http, items),
        _ => Results.Json(new { items }, Json(http)),
    };

    private static string Format(HttpContext http)
    {
        var requested = http.Request.Query["format"].ToString();
        if (!string.IsNullOrEmpty(requested))
        {
            return requested.ToLowerInvariant() switch
            {
                "json" or "csv" or "ndjson" => requested.ToLowerInvariant(),
                _ => throw new ApiFaultException(StatusCodes.Status400BadRequest, "'format' must be json, ndjson or csv."),
            };
        }

        var accept = http.Request.Headers.Accept.ToString();
        return accept.Contains("text/csv", StringComparison.OrdinalIgnoreCase) ? "csv"
            : accept.Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase) ? "ndjson"
            : "json";
    }

    private static JsonSerializerOptions Json(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

    private static IResult NdJson<T>(HttpContext http, IReadOnlyList<T> items)
    {
        var options = Json(http);
        var text = new StringBuilder();
        foreach (var item in items)
        {
            text.Append(JsonSerializer.Serialize(item, options)).Append('\n');
        }

        return Results.Text(text.ToString(), "application/x-ndjson", Encoding.UTF8);
    }

    /// <summary>RFC 4180 with a header row; lists are joined with ';'. Cells are escaped by <see cref="CsvCell"/>.</summary>
    private static IResult Csv<T>(HttpContext http, IReadOnlyList<T> items)
    {
        var options = Json(http);
        var columns = options.GetTypeInfo(typeof(T)).Properties.Select(p => p.Name).ToArray();
        var text = new StringBuilder().AppendJoin(',', columns.Select(c => CsvCell.Escape(c))).Append("\r\n");
        foreach (var item in items)
        {
            var element = JsonSerializer.SerializeToElement(item, options);
            text.AppendJoin(',', columns.Select(c => element.TryGetProperty(c, out var value) ? CsvCell.Escape(Value(value)) : string.Empty)).Append("\r\n");
        }

        return Results.Text(text.ToString(), "text/csv", Encoding.UTF8);
    }

    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Array => string.Join(';', value.EnumerateArray().Select(Value)),
        _ => value.GetRawText(),
    };
}

/// <summary>The <c>granularity</c> of <c>/v1/usage/aggregate</c>: <c>day</c> (default) or <c>month</c>.</summary>
public enum AggregateGranularity
{
    Day,
    Month,
}
