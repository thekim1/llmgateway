using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Stores;
using Ume.LlmGateway.TestKit;

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

        using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);
        using var response = await SendAsync(client, key);

        ((int)response.StatusCode).ShouldBe(500);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>().ShouldBe(GatewayErrorCodes.InternalError);
        var usage = await UsageAsync(gateway, key.Id);
        usage.StatusCode.ShouldBe(500);
        usage.Outcome.ShouldBe(RequestOutcome.ProviderError);
        usage.ErrorCode.ShouldBe(GatewayErrorCodes.InternalError);
        usage.ProviderName.ShouldBe("eu");
        usage.CostUsd.ShouldBe(0);

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

        using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);
        using var response = await SendAsync(client, key);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var usage = await UsageAsync(gateway, key.Id);
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        usage.CostUsd.ShouldBe(0.000135m);
        fixture.Logs.Text.ShouldContain("timed out in the shared store");
    }

    [Fact]
    public async Task Alerts_are_raised_once_per_budget_period_and_threshold_across_batches()
    {
        var key = await fixture.CreateKeyAsync();
        var budget = new Budget { Scope = BudgetScope.VirtualKey, ScopeId = key.Id, LimitUsd = 10, Period = BudgetPeriod.Monthly };
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Monthly, DateTimeOffset.UtcNow);
        var writer = fixture.Services.GetRequiredService<UsageWriter>();
        foreach (var spent in new[] { 5.5m, 6m })
        {
            await writer.EnqueueAsync(new UsageWork(Record(key), [new AlertCandidate(budget, window, 50, spent)]), Ct);
            await writer.FlushAsync(Ct);
        }

        using var scope = fixture.Services.CreateScope();
        var alerts = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AlertEvents.Where(a => a.BudgetId == budget.Id).ToListAsync(Ct);
        alerts.ShouldHaveSingleItem().SpentUsd.ShouldBe(5.5m);
    }

    [Fact]
    public async Task Alert_webhook_gets_the_amounts_in_USD()
    {
        var received = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = fixture.Derive(services =>
        {
            services.PostConfigure<GatewayOptions>(o => o.AlertWebhookUrl = new Uri("https://hooks.test/alerts"));
            services.AddHttpClient(AlertNotifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new CapturingHandler(received));
        });
        var key = await fixture.CreateKeyAsync();
        var budget = new Budget { Scope = BudgetScope.VirtualKey, ScopeId = key.Id, LimitUsd = 10, Period = BudgetPeriod.Monthly };
        var window = BudgetPeriods.GetWindow(BudgetPeriod.Monthly, DateTimeOffset.UtcNow);
        var writer = gateway.Services.GetRequiredService<UsageWriter>();

        await writer.EnqueueAsync(new UsageWork(Record(key), [new AlertCandidate(budget, window, 80, 8.5m)]), Ct);
        await writer.FlushAsync(Ct);

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        payload["type"]!.GetValue<string>().ShouldBe("budget_threshold");
        payload["thresholdPercent"]!.GetValue<int>().ShouldBe(80);
        (payload["spentUsd"]!.GetValue<decimal>(), payload["limitUsd"]!.GetValue<decimal>()).ShouldBe((8.5m, 10m));
        payload["spentSek"].ShouldBeNull();
    }

    private sealed class CapturingHandler(TaskCompletionSource<JsonNode> received) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            received.TrySetResult(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Only_unsuccessful_requests_are_logged_at_information()
    {
        var key = await fixture.CreateKeyAsync();
        using var ok = await SendAsync(fixture.Client, key);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CompletedLogAsync(ok)).Level.ShouldBe(LogLevel.Debug);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"no-such-model","messages":[{"role":"user","content":"Test"}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var refused = await fixture.Client.SendAsync(request, Ct);
        ((int)refused.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
        (await CompletedLogAsync(refused)).Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public async Task Admin_switch_logs_successful_requests_at_information_until_it_expires()
    {
        var key = await fixture.CreateKeyAsync();
        try
        {
            await SetRequestLogAsync(true, until: null);
            using var on = await SendAsync(fixture.Client, key);
            (await CompletedLogAsync(on)).Level.ShouldBe(LogLevel.Information);

            // Expired 24-hour switch: back to Debug without anyone turning it off.
            await SetRequestLogAsync(true, until: DateTimeOffset.UtcNow.AddMinutes(-1));
            using var expired = await SendAsync(fixture.Client, key);
            (await CompletedLogAsync(expired)).Level.ShouldBe(LogLevel.Debug);
        }
        finally
        {
            await SetRequestLogAsync(false, until: null);
        }
    }

    /// <summary>Sets the switch in the one settings row (an upsert: other tests change the same row) and reloads the catalogue.</summary>
    private async Task SetRequestLogAsync(bool on, DateTimeOffset? until)
    {
        var defaults = new GatewaySettings();
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO "GatewaySettings" ("Id", "TrustedProxies", "AuditRetentionDays", "LogAllRequests", "LogAllRequestsUntil")
                 VALUES ({GatewaySettings.SingletonId}, {defaults.TrustedProxies.ToArray()}, {defaults.AuditRetentionDays}, {on}, {until})
                 ON CONFLICT ("Id") DO UPDATE SET "LogAllRequests" = excluded."LogAllRequests", "LogAllRequestsUntil" = excluded."LogAllRequestsUntil"
                 """, Ct);
        }

        fixture.Services.GetRequiredService<GatewayCatalog>().Invalidate();
    }

    /// <summary>The "Request … status … in … ms" entry of a response (accounting may finish just after the answer).</summary>
    private async Task<LogEntry> CompletedLogAsync(HttpResponseMessage response)
    {
        var prefix = $"Request {response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single()} key ";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (fixture.Logs.Entries.FirstOrDefault(e => e.Message.StartsWith(prefix, StringComparison.Ordinal)) is { } entry)
            {
                return entry;
            }

            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public void Usage_copy_writes_every_mapped_column()
    {
        using var scope = fixture.Services.CreateScope();
        var entity = scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Model.FindEntityType(typeof(UsageRecord))!;
        var mapped = entity.GetProperties().Select(p => p.GetColumnName()).Except(["Id", "RecordedAt", GatewayDbContext.LegacyCostSek]);
        UsageRecordCopy.Columns.Order().ShouldBe(mapped.Order());
    }

    [Fact]
    public async Task Usage_batch_round_trips_every_field_and_marks_each_key_used()
    {
        var first = await fixture.CreateKeyAsync();
        var second = await fixture.CreateKeyAsync();
        var full = Record(first);
        Fill(full, new
        {
            Timestamp = new DateTimeOffset(2026, 10, 14, 12, 30, 15, 123, TimeSpan.FromHours(2)),
            Endpoint = GatewayEndpoint.AudioTranscriptions, ProviderAccountId = (Guid?)Guid.NewGuid(), ProviderName = "eu",
            ModelDeploymentId = (Guid?)Guid.NewGuid(), UpstreamModel = "gpt-x", InputTokens = 12, CachedInputTokens = 3, OutputTokens = 45,
            AudioSeconds = 61.5m, CostUsd = 0.00012345m, LatencyMs = 250, UpstreamMs = (int?)200, TimeToFirstTokenMs = (int?)90, ProviderFirstTokenMs = (int?)70,
            Outcome = RequestOutcome.PiiBlocked, FallbackCount = 1, Streamed = true, PiiActionApplied = (PiiPolicy?)PiiPolicy.Redact,
            PiiCategories = "Email:1", ErrorCode = "x", RoutingRuleId = (Guid?)Guid.NewGuid(), RoutingRuleName = "rule",
        });
        var sparse = Record(second);
        sparse.Timestamp = new DateTimeOffset(sparse.Timestamp.Ticks / 10 * 10, TimeSpan.Zero); // Postgres keeps microseconds
        var writer = fixture.Services.GetRequiredService<UsageWriter>();
        await writer.EnqueueAsync(new UsageWork(full, []), Ct);
        await writer.EnqueueAsync(new UsageWork(sparse, []), Ct);
        await writer.FlushAsync(Ct);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var stored = await db.UsageRecords.AsNoTracking().SingleAsync(u => u.RequestId == full.RequestId, Ct);
        ShouldMatch(stored, full);
        ShouldMatch(await db.UsageRecords.AsNoTracking().SingleAsync(u => u.RequestId == sparse.RequestId, Ct), sparse);
        (await db.VirtualKeys.Where(k => k.Id == first.Id || k.Id == second.Id).Select(k => k.LastUsedAt).ToListAsync(Ct)).ShouldAllBe(t => t != null);
    }

    /// <summary>Sets the properties named in <paramref name="values"/> (an anonymous object) on <paramref name="record"/>.</summary>
    private static void Fill(UsageRecord record, object values)
    {
        foreach (var value in values.GetType().GetProperties())
        {
            typeof(UsageRecord).GetProperty(value.Name)!.SetValue(record, value.GetValue(values));
        }
    }

    /// <summary>Every property as written, except those the database assigns (the instant compared, not its offset).</summary>
    private static void ShouldMatch(UsageRecord stored, UsageRecord written)
    {
        stored.Id.ShouldBePositive();
        stored.RecordedAt.ShouldNotBe(default);
        foreach (var property in typeof(UsageRecord).GetProperties().Where(p => p.Name is not ("Id" or "RecordedAt")))
        {
            property.GetValue(stored).ShouldBe(property.GetValue(written), property.Name);
        }
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

        public Task<SpendReservation> ReserveAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, bool missingAsZero, CancellationToken cancellationToken) =>
            _inner.ReserveAsync(counters, amounts, missingAsZero, cancellationToken);

        public async Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> deltas, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }
}
