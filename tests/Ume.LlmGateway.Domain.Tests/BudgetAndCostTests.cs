using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class BudgetPeriodTests
{
    [Fact]
    public void Monthly_window_is_calendar_aligned_in_stockholm_time()
    {
        // 2026-10-31 23:30 UTC is already 2026-11-01 00:30 in Stockholm (CET, UTC+1 after DST ends on Oct 25)
        var now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Monthly, now);

        window.Start.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(1)));
        window.End.ShouldBe(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Monthly_window_across_dst_change_uses_correct_offsets()
    {
        var now = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Monthly, now);

        window.Start.Offset.ShouldBe(TimeSpan.FromHours(1)); // CET
        window.End.Offset.ShouldBe(TimeSpan.FromHours(2));   // CEST
        (window.End - window.Start).ShouldBe(TimeSpan.FromDays(31) - TimeSpan.FromHours(1));
    }

    [Fact]
    public void Weekly_window_starts_on_monday()
    {
        var sunday = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.FromHours(2));
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Weekly, sunday);

        window.Start.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        window.Start.Date.ShouldBe(new DateTime(2026, 10, 5));
        window.End.Date.ShouldBe(new DateTime(2026, 10, 12));
    }

    [Theory]
    [InlineData(2026, 2, 10, 1, 4)]
    [InlineData(2026, 5, 10, 4, 7)]
    [InlineData(2026, 9, 30, 7, 10)]
    [InlineData(2026, 12, 31, 10, 1)]
    public void Quarterly_window(int y, int m, int d, int startMonth, int endMonth)
    {
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Quarterly, new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero));
        window.Start.Month.ShouldBe(startMonth);
        window.End.Month.ShouldBe(endMonth);
    }

    [Fact]
    public void Hourly_window_is_aligned_to_the_hour()
    {
        var now = new DateTimeOffset(2026, 10, 8, 13, 47, 12, TimeSpan.FromHours(2)); // 11:47 UTC
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Hourly, now);
        window.Start.ShouldBe(new DateTimeOffset(2026, 10, 8, 11, 0, 0, TimeSpan.Zero));
        window.End.ShouldBe(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        BudgetPeriods.GetWindow(BudgetPeriod.Hourly, window.End).Start.ShouldBe(window.End);
    }

    [Fact]
    public void Hourly_window_is_one_hour_across_dst_changes()
    {
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Hourly, new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)); // CEST -> CET at 01:00 UTC
        (window.End - window.Start).ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Daily_and_yearly_windows()
    {
        var now = new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero); // 00:30 next day in Stockholm
        BudgetPeriods.GetWindow(BudgetPeriod.Daily, now).Start.Date.ShouldBe(new DateTime(2026, 6, 16));
        BudgetPeriods.GetWindow(BudgetPeriod.Yearly, now).Start.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)));
    }
}

public class BudgetEvaluatorTests
{
    private static readonly PeriodWindow Window = new(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

    private static BudgetState State(BudgetScope scope, decimal limit, decimal spent, bool active = true) =>
        new(new Budget { Scope = scope, ScopeId = Guid.NewGuid(), LimitSek = limit, IsActive = active }, Window, spent);

    [Fact]
    public void Allows_when_all_budgets_have_room() =>
        BudgetEvaluator.Evaluate([State(BudgetScope.VirtualKey, 100, 10), State(BudgetScope.Department, 1000, 999.99m)])
            .Allowed.ShouldBeTrue();

    [Fact]
    public void Blocks_when_any_level_in_hierarchy_is_exhausted()
    {
        var dept = State(BudgetScope.Department, 1000, 1000);
        var decision = BudgetEvaluator.Evaluate([State(BudgetScope.VirtualKey, 100, 10), State(BudgetScope.Team, 500, 20), dept]);

        decision.Allowed.ShouldBeFalse();
        decision.ExceededBudget.ShouldBe(dept);
    }

    [Fact]
    public void Reports_most_specific_exceeded_budget()
    {
        var key = State(BudgetScope.VirtualKey, 10, 11);
        var decision = BudgetEvaluator.Evaluate([State(BudgetScope.Department, 10, 20), key]);
        decision.ExceededBudget.ShouldBe(key);
    }

    [Fact]
    public void Inactive_budgets_are_ignored() =>
        BudgetEvaluator.Evaluate([State(BudgetScope.Team, 10, 50, active: false)]).Allowed.ShouldBeTrue();

    [Fact]
    public void Crossed_thresholds_are_reported_once()
    {
        var budget = new Budget { LimitSek = 1000, AlertThresholds = [50, 80, 100] };

        BudgetEvaluator.CrossedThresholds(budget, 400, 450).ShouldBeEmpty();
        BudgetEvaluator.CrossedThresholds(budget, 450, 850).ShouldBe([50, 80]);
        BudgetEvaluator.CrossedThresholds(budget, 850, 1200).ShouldBe([100]);
        BudgetEvaluator.CrossedThresholds(budget, 1200, 1300).ShouldBeEmpty();
    }

    [Fact]
    public void Percent_used_is_rounded() => BudgetEvaluator.PercentUsed(1, 3).ShouldBe(33.3m);
}

public class CostCalculatorTests
{
    private static readonly ModelPrice Price = new()
    {
        InputPerMillionUsd = 2.50m,
        CachedInputPerMillionUsd = 1.25m,
        OutputPerMillionUsd = 10m,
    };

    [Fact]
    public void Calculates_usd_and_sek()
    {
        var cost = CostCalculator.Calculate(new TokenUsage(1_000_000, 0, 100_000), Price, 10.5m);
        cost.Usd.ShouldBe(3.5m);
        cost.Sek.ShouldBe(36.75m);
    }

    [Fact]
    public void Cached_tokens_are_billed_at_cached_rate()
    {
        var cost = CostCalculator.Calculate(new TokenUsage(1_000_000, 400_000, 0), Price, 1m);
        cost.Usd.ShouldBe((600_000 * 2.5m + 400_000 * 1.25m) / 1_000_000m);
    }

    [Fact]
    public void Cached_tokens_cannot_exceed_input() =>
        CostCalculator.Calculate(new TokenUsage(100, 500, 0), Price, 1m).Usd.ShouldBe(100 * 1.25m / 1_000_000m);

    [Fact]
    public void Missing_price_is_free() =>
        CostCalculator.Calculate(new TokenUsage(1000, 0, 1000), null, 10m).ShouldBe(new Cost(0, 0));

    [Fact]
    public void Price_history_picks_latest_effective_price()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var deployment = new ModelDeployment
        {
            Name = "x",
            UpstreamModel = "x",
            Prices =
            [
                new ModelPrice { EffectiveFrom = t0, InputPerMillionUsd = 1 },
                new ModelPrice { EffectiveFrom = t0.AddMonths(6), InputPerMillionUsd = 2 },
            ],
        };

        deployment.PriceAt(t0.AddMonths(3))!.InputPerMillionUsd.ShouldBe(1);
        deployment.PriceAt(t0.AddMonths(7))!.InputPerMillionUsd.ShouldBe(2);
        deployment.PriceAt(t0.AddDays(-1)).ShouldBeNull();
    }

    [Fact]
    public void Estimate_tokens_is_at_least_one()
    {
        CostCalculator.EstimateTokens(0).ShouldBe(1);
        CostCalculator.EstimateTokens(400).ShouldBe(100);
    }

    [Fact]
    public void Audio_is_billed_per_minute_on_top_of_tokens()
    {
        var price = new ModelPrice { InputPerMillionUsd = 6, OutputPerMillionUsd = 10, AudioPerMinuteUsd = 0.006m };
        CostCalculator.Calculate(new TokenUsage(0, 0, 0, 90), price, 10).ShouldBe(new Cost(0.009m, 0.09m));
        CostCalculator.Calculate(new TokenUsage(1_000_000, 0, 0, 60), price, 10).Usd.ShouldBe(6.006m);
    }

    [Fact]
    public void Transcription_estimate_covers_both_pricing_models()
    {
        var estimate = CostCalculator.EstimateTranscription(60);
        estimate.ShouldBe(new TokenUsage(60 * CostCalculator.AudioTokensPerSecond, 0, 60 * CostCalculator.TranscriptTokensPerSecond, 60,
            AudioInputTokens: 60 * CostCalculator.AudioTokensPerSecond));
    }

    [Fact]
    public void Audio_tokens_are_billed_at_the_audio_price_when_one_is_set()
    {
        // gpt-realtime-like: text 4/16, audio 32/64 USD per million.
        var price = new ModelPrice { InputPerMillionUsd = 4, CachedInputPerMillionUsd = 0.4m, OutputPerMillionUsd = 16, AudioInputPerMillionUsd = 32, AudioOutputPerMillionUsd = 64 };
        var usage = new TokenUsage(1_000_000, 0, 1_000_000, AudioInputTokens: 750_000, AudioOutputTokens: 500_000);
        CostCalculator.Calculate(usage, price, 10).Usd.ShouldBe(1 + 24 + 8 + 32m);

        // Without audio prices the audio tokens are text tokens, as before.
        CostCalculator.Calculate(usage, new ModelPrice { InputPerMillionUsd = 4, OutputPerMillionUsd = 16 }, 10).Usd.ShouldBe(20m);

        // Audio tokens never exceed the uncached input: cached tokens keep their own rate.
        CostCalculator.Calculate(new TokenUsage(100, 50, 0, AudioInputTokens: 100), price, 10).Usd.ShouldBe((50 * 32 + 50 * 0.4m) / 1_000_000);
    }

    [Fact]
    public void Live_session_estimate_adds_spoken_output()
    {
        var estimate = CostCalculator.EstimateRealtime(60);
        var spoken = 60 * CostCalculator.AudioTokensPerSecond;
        estimate.AudioOutputTokens.ShouldBe(spoken);
        estimate.OutputTokens.ShouldBe(60 * CostCalculator.TranscriptTokensPerSecond + spoken);
        estimate.AudioSeconds.ShouldBe(60);
    }

    [Fact]
    public void Token_usage_adds_up()
    {
        (new TokenUsage(1, 2, 3, 4, 5, 6) + new TokenUsage(10, 20, 30, 40, 50, 60)).ShouldBe(new TokenUsage(11, 22, 33, 44, 55, 66));
    }

    [Fact]
    public void Estimate_input_tokens_counts_images_per_image_not_by_base64_size()
    {
        // 4 MB body, almost all of it one base64 photo: one image's worth of tokens plus the text around it.
        var scan = new AttachmentScan(AttachmentKinds.Image, Images: 1, ImageChars: 4_000_000);
        CostCalculator.EstimateInputTokens(4_000_400, scan).ShouldBe(100 + CostCalculator.ImageTokenEstimate);
        CostCalculator.EstimateInputTokens(400, default).ShouldBe(100);
        // Documents, audio and video stay size-based.
        CostCalculator.EstimateInputTokens(4_000_000, new AttachmentScan(AttachmentKinds.Video, 0, 0)).ShouldBe(1_000_000);
    }
}
