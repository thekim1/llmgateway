namespace Ume.LlmGateway.TestKit;

/// <summary>
/// Keeps tests that run against the real clock away from the boundaries where the gateway's windows roll over: rate limits
/// count per calendar minute (UTC), and budget periods start at midnight in Europe/Stockholm. A test that makes several
/// requests and expects them in the same window waits for the next window when the current one is nearly over, instead of
/// failing when the boundary falls between its requests.
/// </summary>
public static class TestClock
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    /// <summary>Returns once at least <paramref name="needed"/> (default 10 s) is left of the current rate-limit minute.</summary>
    public static Task InOneRateLimitMinuteAsync(TimeSpan? needed = null) =>
        WaitIfWithinAsync(UntilNextMinute(DateTimeOffset.UtcNow), needed ?? TimeSpan.FromSeconds(10));

    /// <summary>Returns once at least <paramref name="needed"/> (default 30 s) is left before midnight in Stockholm.</summary>
    public static Task InOneBudgetDayAsync(TimeSpan? needed = null) =>
        WaitIfWithinAsync(UntilStockholmMidnight(DateTimeOffset.UtcNow), needed ?? TimeSpan.FromSeconds(30));

    private static TimeSpan UntilNextMinute(DateTimeOffset now) => TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(now.UtcTicks % TimeSpan.TicksPerMinute);

    private static TimeSpan UntilStockholmMidnight(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Stockholm);
        var midnight = new DateTimeOffset(local.Date.AddDays(1), Stockholm.GetUtcOffset(local.Date.AddDays(1)));
        return midnight - now;
    }

    private static async Task WaitIfWithinAsync(TimeSpan left, TimeSpan needed)
    {
        if (left < needed)
        {
            // Past the boundary, plus a little for clock differences between the test and the host it calls.
            await Task.Delay(left + TimeSpan.FromMilliseconds(250));
        }
    }
}
