using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Budgets;
using Ume.LlmGateway.Gateway.Requests;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Token budgets: quotas of input plus output tokens, optionally counting only providers of one residency.</summary>
public sealed class TokenQuotaTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BudgetService Service(ISpendLedger ledger) => new(ledger, fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

    private static Budget OnPremQuota(Guid teamId, long limit) => new()
    {
        Scope = BudgetScope.Team, ScopeId = teamId, Unit = BudgetUnit.Tokens, LimitTokens = limit, Residency = DataResidency.OnPrem,
        Period = BudgetPeriod.Daily, AlertThresholds = [50, 100], CreatedAt = DateTimeOffset.UtcNow,
    };

    private static string CounterKey(Budget budget) => SpendCounter.KeyFor(budget, BudgetPeriods.GetWindow(budget.Period, DateTimeOffset.UtcNow).Start);

    [Fact]
    public async Task A_residency_quota_applies_only_when_a_candidate_has_that_residency()
    {
        var ledger = new InMemorySpendLedger();
        var service = Service(ledger);
        var quota = OnPremQuota(Guid.NewGuid(), 1000);

        (await service.ReserveAsync([quota], 0.01m, 400, [DataResidency.Eu], Ct)).Checks.ShouldBeEmpty(); // EU only: not counted

        var reservation = await service.ReserveAsync([quota], 0.01m, 400, [DataResidency.OnPrem, DataResidency.Eu], Ct);
        reservation.Allowed.ShouldBeTrue();
        reservation.ReservedTokens.ShouldBe(400);
        reservation.RemainingUsd.ShouldBeNull(); // no cost budget applies
        (await ledger.GetAsync([CounterKey(quota)], Ct))[0].ShouldBe(400);

        // Served on-prem: the actual tokens replace the reservation, and the 50 % alert is raised.
        var alerts = await service.CommitAsync(reservation, 0, 600, DataResidency.OnPrem, Ct);
        (await ledger.GetAsync([CounterKey(quota)], Ct))[0].ShouldBe(600);
        alerts.ShouldHaveSingleItem().ThresholdPercent.ShouldBe(50);
        alerts[0].SpentUsd.ShouldBe(600); // in the budget's unit

        // Served in the EU after all (fallback): the reserved tokens go back.
        var other = await service.ReserveAsync([quota], 0, 300, [DataResidency.OnPrem, DataResidency.Eu], Ct);
        await service.CommitAsync(other, 0, 300, DataResidency.Eu, Ct);
        (await ledger.GetAsync([CounterKey(quota)], Ct))[0].ShouldBe(600);

        // Full: refused, while a cost budget alongside is reserved in the same call.
        var cost = new Budget { Scope = BudgetScope.Team, ScopeId = quota.ScopeId, LimitUsd = 100, Period = BudgetPeriod.Daily, CreatedAt = DateTimeOffset.UtcNow };
        var refused = await service.ReserveAsync([cost, quota], 1m, 500, [DataResidency.OnPrem], Ct);
        refused.Allowed.ShouldBeFalse();
        refused.Exceeded!.Budget.ShouldBeSameAs(quota);
        var fits = await service.ReserveAsync([cost, quota], 1m, 400, [DataResidency.OnPrem], Ct);
        fits.Allowed.ShouldBeTrue();
        fits.RemainingUsd.ShouldBe(100);
    }

    [Fact]
    public void Thresholds_and_the_refusal_are_in_tokens()
    {
        var quota = OnPremQuota(Guid.NewGuid(), 1_000_000);
        BudgetEvaluator.CrossedThresholds(quota, 400_000, 1_000_000).ShouldBe([50, 100]);
        var error = GatewayError.BudgetExceeded(quota, "Contact the budget owner.");
        error.StatusCode.ShouldBe(402);
        error.Message.ShouldContain("token quota (1,000,000 tokens per day on OnPrem models)");
    }

    [Fact]
    public async Task On_prem_quota_is_enforced_by_the_gateway_and_seeded_from_recorded_usage()
    {
        var key = await fixture.CreateKeyAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.Budgets.Add(new Budget
            {
                Scope = BudgetScope.Team, ScopeId = key.TeamId, Unit = BudgetUnit.Tokens, LimitTokens = 250, Residency = DataResidency.OnPrem,
                Period = BudgetPeriod.Daily, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        // The mock provider reports 100 + 20 tokens per answer: two on-prem answers fit, the third is refused.
        for (var i = 0; i < 2; i++)
        {
            using var ok = await fixture.SendAsync(key, "onprem/ok");
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
            await fixture.UsageAsync(ok);
        }

        using var refused = await fixture.SendAsync(key, "onprem/ok");
        refused.StatusCode.ShouldBe((HttpStatusCode)402);
        var error = (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["error"]!;
        error["code"]!.GetValue<string>().ShouldBe("budget_exceeded");
        error["message"]!.GetValue<string>().ShouldContain("token quota");

        // The quota counts on-prem models only: EU models still answer.
        using var eu = await fixture.SendAsync(key, "eu/ok");
        eu.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A cold counter (another instance, a new Redis) is seeded from the usage records: 240 used.
        var quota = new Budget
        {
            Scope = BudgetScope.Team, ScopeId = key.TeamId, Unit = BudgetUnit.Tokens, LimitTokens = 250, Residency = DataResidency.OnPrem,
            Period = BudgetPeriod.Daily,
        };
        using (var scope = fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().SpentAsync(quota, BudgetPeriods.GetWindow(BudgetPeriod.Daily, DateTimeOffset.UtcNow), Ct))
                .ShouldBe(240);
        }
    }
}
