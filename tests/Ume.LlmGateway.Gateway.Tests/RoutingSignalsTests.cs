using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>The read-only budget and token signals that routing rules can use (<c>budget_used</c>, <c>tokens_used</c>).</summary>
public sealed class RoutingSignalsTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Budget TeamBudget(Guid teamId, decimal limit) =>
        new() { Scope = BudgetScope.Team, ScopeId = teamId, LimitSek = limit, Period = BudgetPeriod.Daily, CreatedAt = DateTimeOffset.UtcNow };

    private BudgetService Service(ISpendLedger ledger, TimeProvider? time = null) =>
        new(ledger, fixture.Services.GetRequiredService<IServiceScopeFactory>(), time ?? TimeProvider.System);

    [Fact]
    public async Task No_budget_means_no_value()
    {
        (await Service(new InMemorySpendLedger()).PeekUsedPercentAsync([], Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Peek_reports_spend_as_a_percentage_of_the_limit()
    {
        var ledger = new InMemorySpendLedger();
        var service = Service(ledger);
        var budget = TeamBudget(Guid.NewGuid(), 200m);

        (await service.PeekUsedPercentAsync([budget], Ct)).ShouldBe(0);

        var counter = new SpendCounter(SpendCounter.KeyFor(budget.Scope, budget.ScopeId, budget.Period, BudgetPeriods.GetWindow(budget.Period, DateTimeOffset.UtcNow).Start), 200_000_000, TimeSpan.FromDays(2));
        await ledger.AddAsync([counter], 50_000_000, Ct); // 50 SEK
        (await service.PeekUsedPercentAsync([budget], Ct)).ShouldBe(25);
        await ledger.AddAsync([counter], 500_000_000, Ct); // overspend
        (await service.PeekUsedPercentAsync([budget], Ct)).ShouldBe(100);
    }

    [Fact]
    public async Task Peek_uses_the_budget_closest_to_its_limit()
    {
        var ledger = new InMemorySpendLedger();
        var service = Service(ledger);
        var roomy = TeamBudget(Guid.NewGuid(), 1000m);
        var tight = new Budget { Scope = BudgetScope.Department, ScopeId = Guid.NewGuid(), LimitSek = 10m, Period = BudgetPeriod.Monthly, CreatedAt = DateTimeOffset.UtcNow };
        var now = DateTimeOffset.UtcNow;
        await ledger.InitializeAsync(SpendCounter.KeyFor(roomy.Scope, roomy.ScopeId, roomy.Period, BudgetPeriods.GetWindow(roomy.Period, now).Start), 100_000_000, TimeSpan.FromDays(2), Ct); // 10 %
        await ledger.InitializeAsync(SpendCounter.KeyFor(tight.Scope, tight.ScopeId, tight.Period, BudgetPeriods.GetWindow(tight.Period, now).Start), 8_000_000, TimeSpan.FromDays(40), Ct); // 80 %

        (await service.PeekUsedPercentAsync([roomy, tight], Ct)).ShouldBe(80);
    }

    [Fact]
    public async Task Peek_does_not_reserve_or_change_anything()
    {
        var ledger = new InMemorySpendLedger();
        var service = Service(ledger);
        var budget = TeamBudget(Guid.NewGuid(), 100m);
        var key = SpendCounter.KeyFor(budget.Scope, budget.ScopeId, budget.Period, BudgetPeriods.GetWindow(budget.Period, DateTimeOffset.UtcNow).Start);

        for (var i = 0; i < 5; i++)
        {
            await service.PeekUsedPercentAsync([budget], Ct);
        }

        (await ledger.GetAsync([key], Ct))[0].ShouldBe(0); // seeded, never incremented

        var reservation = await service.ReserveAsync([budget], 10m, Ct);
        reservation.Allowed.ShouldBeTrue();
        (await service.PeekUsedPercentAsync([budget], Ct)).ShouldBe(10); // in-flight reservations count, as in enforcement
        (await service.PeekUsedPercentAsync([budget], Ct)).ShouldBe(10);
    }

    [Fact]
    public async Task Peek_seeds_a_cold_counter_from_recorded_usage()
    {
        var teamId = Guid.NewGuid();
        var requestId = "peek-" + Guid.NewGuid().ToString("N");
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.Add(new UsageRecord
            {
                RequestId = requestId, RequestedModel = "m", Timestamp = DateTimeOffset.UtcNow, TeamId = teamId,
                VirtualKeyId = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), CostSek = 40m,
            });
            await db.SaveChangesAsync(Ct);
        }

        try
        {
            // A fresh ledger knows nothing (cold start), so the service must read the usage table.
            (await Service(new InMemorySpendLedger()).PeekUsedPercentAsync([TeamBudget(teamId, 100m)], Ct)).ShouldBe(40);
        }
        finally
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.Where(u => u.RequestId == requestId).ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task A_zero_limit_budget_counts_as_full()
    {
        (await Service(new InMemorySpendLedger()).PeekUsedPercentAsync([TeamBudget(Guid.NewGuid(), 0m)], Ct)).ShouldBe(100);
    }

    [Fact]
    public async Task Token_usage_is_a_percentage_of_the_per_minute_limit_without_counting_a_request()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 10, TimeSpan.Zero));
        var limiter = new InMemoryRateLimiter(time);
        var key = Guid.NewGuid();

        (await limiter.PeekTokensUsedPercentAsync(key, null, Ct)).ShouldBeNull(); // no limit configured
        (await limiter.PeekTokensUsedPercentAsync(key, 0, Ct)).ShouldBeNull();
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Ct)).ShouldBe(0);

        await limiter.RecordTokensAsync(key, 250, Ct);
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Ct)).ShouldBe(25);
        await limiter.RecordTokensAsync(key, 5000, Ct);
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Ct)).ShouldBe(100); // clamped

        // Peeking never consumed request quota: with a limit of 1 request per minute the first real call still passes.
        var other = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await limiter.PeekTokensUsedPercentAsync(other, 1000, Ct);
        }

        (await limiter.AcquireAsync(other, 1, null, Ct)).Allowed.ShouldBeTrue();

        // A new minute starts from zero.
        time.Advance(TimeSpan.FromSeconds(60));
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Ct)).ShouldBe(0);
    }
}
