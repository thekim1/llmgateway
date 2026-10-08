namespace Ume.LlmGateway.Domain.Services;

public readonly record struct PeriodWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Calendar-aligned budget periods in the Europe/Stockholm time zone (so "this month" matches how
/// förvaltningar think about cost, including DST changes).
/// </summary>
public static class BudgetPeriods
{
    public static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public static PeriodWindow GetWindow(BudgetPeriod period, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= Stockholm;
        if (period == BudgetPeriod.Hourly)
        {
            // Stockholm's UTC offset is always a whole number of hours, so UTC hours coincide with local hours.
            var hourStart = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
            return new PeriodWindow(hourStart, hourStart.AddHours(1));
        }

        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var date = local.Date;

        DateTime startLocal = period switch
        {
            BudgetPeriod.Daily => date,
            BudgetPeriod.Weekly => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), // ISO week, Monday start
            BudgetPeriod.Monthly => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
            BudgetPeriod.Quarterly => new DateTime(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            BudgetPeriod.Yearly => new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };

        DateTime endLocal = period switch
        {
            BudgetPeriod.Daily => startLocal.AddDays(1),
            BudgetPeriod.Weekly => startLocal.AddDays(7),
            BudgetPeriod.Monthly => startLocal.AddMonths(1),
            BudgetPeriod.Quarterly => startLocal.AddMonths(3),
            BudgetPeriod.Yearly => startLocal.AddYears(1),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };

        return new PeriodWindow(ToOffset(startLocal, zone), ToOffset(endLocal, zone));
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
