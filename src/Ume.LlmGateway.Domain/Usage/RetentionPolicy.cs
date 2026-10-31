using Ume.LlmGateway.Domain.Budgets;
namespace Ume.LlmGateway.Domain.Usage;

/// <summary>
/// Limits and dates for the nightly removal of old usage, refused-credential, alert and audit rows. The removal itself is the
/// database function <c>ume_retention_step</c> (migration <c>DataRetention</c>), which applies the same rules.
/// </summary>
public static class RetentionPolicy
{
    public const int DefaultDays = 365;

    /// <summary>
    /// The shortest retention. Budget windows up to a quarter, the provider stability report (90 days) and the daily usage
    /// chart then always fall within kept records; longer budget periods start on a month and read monthly totals.
    /// </summary>
    public const int MinimumDays = 90;

    public const int MaximumDays = 3650;

    /// <summary>
    /// Records before this instant are removed: the start of the Europe/Stockholm month that contains
    /// <c>now - days</c>, so whole months go and at least <paramref name="days"/> days are always kept.
    /// </summary>
    public static DateTimeOffset UsageCutoff(DateTimeOffset now, int days)
    {
        var local = TimeZoneInfo.ConvertTime(now.AddDays(-Math.Max(days, MinimumDays)), BudgetPeriods.Stockholm).DateTime;
        var monthStart = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(monthStart, BudgetPeriods.Stockholm.GetUtcOffset(monthStart)).ToUniversalTime();
    }
}
