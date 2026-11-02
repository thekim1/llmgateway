using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Budgets;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Requests;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Gateway.Budgets;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// When no routing rule can apply to the key, the rate limit and the budget reservation are one call
/// (<see cref="IAdmissionStore"/>, one round trip to Redis). The answers are the ones the two separate steps give.
/// </summary>
public sealed class AdmissionTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rate_limit_and_budget_are_one_call_unless_a_rule_can_apply_to_the_key()
    {
        var limited = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 100);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, limited.Id, 10);
        // Without a rate limit there is only the reservation to make, so the separate steps are used.
        var unlimited = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, unlimited.Id, 10);
        // A rule of the key may change the model, so the estimate waits for routing.
        var ruled = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 100);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, ruled.Id, 10);
        var rule = await AddRuleAsync(ruled);
        try
        {
            // Derived after the rule exists, so its catalogue has it from the first request.
            var counting = new CountingAdmission();
            await using var gateway = fixture.Derive(services =>
            {
                services.RemoveAll<IAdmissionStore>();
                services.AddSingleton<IAdmissionStore>(sp =>
                    counting.Wrap(new SequentialAdmissionStore(sp.GetRequiredService<IRateLimiter>(), sp.GetRequiredService<ISpendLedger>())));
            });
            using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);

            using (var response = await SendAsync(client, limited))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                response.Headers.GetValues("x-ratelimit-remaining-requests").Single().ShouldBe("99");
                response.Headers.Contains("x-ume-budget-remaining-usd").ShouldBeTrue();
            }

            counting.Calls.ShouldBe(1);

            using (var response = await SendAsync(client, unlimited))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                response.Headers.Contains("x-ume-budget-remaining-usd").ShouldBeTrue();
            }

            using (var response = await SendAsync(client, ruled))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                response.Headers.GetValues("x-ume-rule").Single().ShouldBe(rule.ToString("D"));
                response.Headers.GetValues("x-ratelimit-remaining-requests").Single().ShouldBe("99");
            }

            counting.Calls.ShouldBe(1);
        }
        finally
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().RoutingRules.Where(r => r.Id == rule).ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task A_request_refused_after_the_reservation_gives_it_back()
    {
        var key = await fixture.CreateKeyAsync(k => { k.RequestsPerMinute = 100; k.PiiPolicy = PiiPolicy.Block; });
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10);

        using var response = await fixture.SendAsync(key, prompt: "Personnummer 19121212-1212");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.UsageAsync(response)).Outcome.ShouldBe(RequestOutcome.PiiBlocked);
        (await SpentAsync(key)).ShouldBe(0);
    }

    [Fact]
    public async Task A_rate_limited_request_reserves_nothing()
    {
        var key = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 1);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10);
        await TestClock.InOneRateLimitMinuteAsync();
        using var first = await fixture.SendAsync(key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cost = (await fixture.UsageAsync(first)).CostUsd;
        (await SpentAsync(key)).ShouldBe(BudgetService.ToMicro(cost));

        using var second = await fixture.SendAsync(key);

        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await fixture.UsageAsync(second)).Outcome.ShouldBe(RequestOutcome.RateLimited);
        (await SpentAsync(key)).ShouldBe(BudgetService.ToMicro(cost));
    }

    [Fact]
    public async Task A_request_over_budget_still_counts_against_the_rate_limit()
    {
        var key = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 1);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 0);
        await TestClock.InOneRateLimitMinuteAsync();

        using var first = await fixture.SendAsync(key);
        first.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        using var second = await fixture.SendAsync(key);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_estimate_counts_the_request_policy_before_it_is_applied()
    {
        // 100 000 output tokens would cost 0.20 USD, over the budget; the policy caps them at 100 (0.0002 USD).
        var key = await fixture.CreateKeyAsync(k => { k.RequestsPerMinute = 100; k.RequestPolicy = new RequestPolicy { MaxOutputTokens = 100 }; });
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 0.01m);

        using var response = await fixture.SendRawAsync(key, "/v1/chat/completions",
            """{"model":"eu/ok","messages":[{"role":"user","content":"Test"}],"max_tokens":100000}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<long?> SpentAsync(TestKey key) =>
        (await fixture.Services.GetRequiredService<ISpendLedger>().GetAsync(
            [SpendCounter.KeyFor(BudgetScope.VirtualKey, key.Id, BudgetPeriod.Monthly, BudgetPeriods.GetWindow(BudgetPeriod.Monthly, DateTimeOffset.UtcNow).Start)], Ct))[0];

    private async Task<Guid> AddRuleAsync(TestKey key)
    {
        var rule = new RoutingRule
        {
            Name = "Admission " + Guid.NewGuid().ToString("N"),
            Condition = "",
            Scope = RoutingScope.VirtualKey,
            ScopeIds = [key.Id],
            Targets = [new RoutingRuleTarget { Model = "eu/ok" }],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync(Ct);
        return rule.Id;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, TestKey key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"eu/ok","messages":[{"role":"user","content":"Test"}],"max_tokens":20}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        return await client.SendAsync(request, Ct);
    }

    private sealed class CountingAdmission
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IAdmissionStore Wrap(IAdmissionStore inner) => new Store(this, inner);

        private sealed class Store(CountingAdmission owner, IAdmissionStore inner) : IAdmissionStore
        {
            public Task<Admission> AdmitAsync(RateLimitRequest rate, IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                return inner.AdmitAsync(rate, counters, amounts, cancellationToken);
            }
        }
    }
}
