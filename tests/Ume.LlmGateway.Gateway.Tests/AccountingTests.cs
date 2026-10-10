using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Once a budget is reserved, every request ends with the reservation reconciled and one usage record.</summary>
public sealed class AccountingTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unexpected_exception_mid_attempt_releases_the_reservation_and_records_usage()
    {
        await using var gateway = fixture.Derive(services => services.Insert(0, ServiceDescriptor.Singleton<IProviderAdapter>(new ThrowingAdapter())));
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10);

        using var client = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var response = await SendAsync(client, key);

        ((int)response.StatusCode).ShouldBe(500);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>().ShouldBe(GatewayErrorCodes.InternalError);
        var usage = await UsageAsync(gateway, key.Id);
        usage.StatusCode.ShouldBe(500);
        usage.Outcome.ShouldBe(RequestOutcome.ProviderError);
        usage.ErrorCode.ShouldBe(GatewayErrorCodes.InternalError);
        usage.ProviderName.ShouldBe("eu");
        usage.CostSek.ShouldBe(0);

        // The estimate reserved before the attempt is given back in full.
        (await gateway.Services.GetRequiredService<ISpendLedger>().GetAsync([SpendKey(key.Id)], Ct))[0].ShouldBe(0);
    }

    [Fact]
    public async Task Usage_is_recorded_when_the_accounting_store_times_out()
    {
        await using var gateway = fixture.Derive(services =>
        {
            services.RemoveAll<ISpendLedger>();
            services.AddSingleton<ISpendLedger, HangingCommitLedger>();
        });
        gateway.Services.GetRequiredService<GatewayRequestHandler>().AccountingTimeout = TimeSpan.FromMilliseconds(200);
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10);

        using var client = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var response = await SendAsync(client, key);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var usage = await UsageAsync(gateway, key.Id);
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        usage.CostSek.ShouldBe(0.00135m);
        fixture.Logs.Text.ShouldContain("timed out in the shared store");
    }

    [Fact]
    public async Task Alerts_are_raised_once_per_budget_period_and_threshold_across_batches()
    {
        var key = await fixture.CreateKeyAsync();
        var budget = new Budget { Scope = BudgetScope.VirtualKey, ScopeId = key.Id, LimitSek = 10, Period = BudgetPeriod.Monthly };
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Monthly, DateTimeOffset.UtcNow);
        var writer = fixture.Services.GetRequiredService<UsageWriter>();
        foreach (var spent in new[] { 5.5m, 6m })
        {
            await writer.EnqueueAsync(new UsageWork(Record(key), [new AlertCandidate(budget, window, 50, spent)]), Ct);
            await writer.FlushAsync(Ct);
        }

        using var scope = fixture.Services.CreateScope();
        var alerts = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AlertEvents.Where(a => a.BudgetId == budget.Id).ToListAsync(Ct);
        alerts.ShouldHaveSingleItem().SpentSek.ShouldBe(5.5m);
    }

    private UsageRecord Record(TestKey key) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), VirtualKeyId = key.Id, TeamId = key.TeamId, DepartmentId = fixture.DepartmentId,
        Timestamp = DateTimeOffset.UtcNow, RequestedModel = "test", StatusCode = 200,
    };

    private static string SpendKey(Guid keyId) =>
        SpendCounter.KeyFor(BudgetScope.VirtualKey, keyId, BudgetPeriod.Monthly, BudgetPeriods.GetWindow(BudgetPeriod.Monthly, DateTimeOffset.UtcNow).Start);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, TestKey key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"eu/ok","messages":[{"role":"user","content":"Test"}],"max_tokens":20}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<UsageRecord> UsageAsync(WebApplicationFactory<Program> gateway, Guid keyId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await gateway.Services.GetRequiredService<UsageWriter>().FlushAsync(cts.Token);
            using var scope = gateway.Services.CreateScope();
            var record = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.SingleOrDefaultAsync(u => u.VirtualKeyId == keyId, cts.Token);
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(50, cts.Token);
        }
    }

    private sealed class ThrowingAdapter : IProviderAdapter
    {
        public bool CanHandle(ProviderType type) => true;

        public Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated adapter bug");
    }

    /// <summary>Reserves normally; reconciling never answers (until the caller gives up).</summary>
    private sealed class HangingCommitLedger : ISpendLedger
    {
        private readonly InMemorySpendLedger _inner = new();

        public Task<IReadOnlyList<long?>> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) => _inner.GetAsync(keys, cancellationToken);

        public Task InitializeAsync(string key, long valueMicroSek, TimeSpan timeToLive, CancellationToken cancellationToken) =>
            _inner.InitializeAsync(key, valueMicroSek, timeToLive, cancellationToken);

        public Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, long amountMicroSek, bool missingAsZero, CancellationToken cancellationToken) =>
            _inner.ReserveAsync(counters, amountMicroSek, missingAsZero, cancellationToken);

        public async Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, long deltaMicroSek, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }
}
