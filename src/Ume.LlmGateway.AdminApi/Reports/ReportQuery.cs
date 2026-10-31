namespace Ume.LlmGateway.AdminApi.Reports;

/// <summary>The paging and time range query parameters every report shares, checked the same way.</summary>
internal static class ReportQuery
{
    public static (int Skip, int Take) Pagination(int? page, int? pageSize)
    {
        var number = page ?? 1; var size = pageSize ?? 50;
        if (number is < 1 or > 100000 || size is < 1 or > 200) { throw new ApiFaultException(400, "The page number or page size is invalid."); }
        return ((number - 1) * size, size);
    }

    /// <summary>From <paramref name="from"/> (default: 30 days ago) to <paramref name="to"/> (default: now), at most a year.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        var start = from?.ToUniversalTime() ?? now.AddDays(-30);
        var end = to?.ToUniversalTime() ?? now;
        if (start >= end || end - start > TimeSpan.FromDays(366)) { throw new ApiFaultException(400, "Choose a time range of at most one year."); }
        return (start, end);
    }
}
