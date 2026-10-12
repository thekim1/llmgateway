using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>The Usage dashboard figures on <c>/api/usage/metrics</c>.</summary>
public sealed class UsageMetricsTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Metrics_are_bucketed_aggregated_and_department_scoped()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        var other = await fixture.OrganisationAsync(admin);
        var now = DateTimeOffset.UtcNow;
        var first = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-3);
        var second = first.AddHours(1);
        var provider = Guid.NewGuid();
        await SaveAsync(
        [
            Record(own, first.AddMinutes(5), "m-a", RequestOutcome.Success, provider, latency: 1000, upstream: 900, input: 1000, cached: 400, output: 100, cost: 0.10m),
            Record(own, first.AddMinutes(10), "m-a", RequestOutcome.Success, provider, latency: 2000, upstream: 1950, firstToken: 300, input: 1000, output: 300, cost: 0.20m),
            Record(own, first.AddMinutes(15), "m-b", RequestOutcome.ProviderError, provider, latency: 500, upstream: 480, fallbacks: 1),
            Record(own, second.AddMinutes(5), "m-b", RequestOutcome.RateLimited, null, latency: 2), // refused before routing: no timing
            Record(own, second.AddMinutes(10), "m-a", RequestOutcome.ClientCancelled, provider, latency: 100, upstream: 90),
            // A live session: its length is not latency.
            Record(own, second.AddMinutes(20), "m-live", RequestOutcome.Success, provider, latency: 600_000, endpoint: GatewayEndpoint.Realtime, cost: 0.05m),
            Record(other, first.AddMinutes(5), "m-a", RequestOutcome.Success, provider, latency: 50, upstream: 40, cost: 9m),
        ]);
        var window = $"from={Iso(first)}&to={Iso(first.AddHours(3))}";

        using var viewer = await fixture.ClientAsync("viewer", own.Code);
        var metrics = await GetAsync(viewer, window);
        metrics["bucket"]!.GetValue<string>().ShouldBe("hour"); // auto: up to two days
        metrics["recordsFrom"].ShouldBeNull();
        var totals = metrics["totals"]!;
        totals["requests"]!.GetValue<long>().ShouldBe(6);
        totals["successes"]!.GetValue<long>().ShouldBe(2 + 1);
        totals["providerErrors"]!.GetValue<long>().ShouldBe(1);
        totals["refused"]!.GetValue<long>().ShouldBe(1);
        totals["cancelled"]!.GetValue<long>().ShouldBe(1);
        totals["fallbacks"]!.GetValue<long>().ShouldBe(1);
        totals["costUsd"]!.GetValue<decimal>().ShouldBe(0.35m);
        totals["inputTokens"]!.GetValue<long>().ShouldBe(2000);
        totals["cachedInputTokens"]!.GetValue<long>().ShouldBe(400);
        totals["outputTokens"]!.GetValue<long>().ShouldBe(400);
        totals["cacheHitRate"]!.GetValue<double>().ShouldBe(0.2, 1e-9);
        // Latency over 100, 500, 1000 and 2000 ms (not the refusal or the session); nearest-rank percentiles.
        totals["latency"]!["avg"]!.GetValue<double>().ShouldBe(900, 1e-9);
        totals["latency"]!["p50"]!.GetValue<int>().ShouldBe(500);
        totals["latency"]!["p90"]!.GetValue<int>().ShouldBe(2000);
        totals["latency"]!["p99"]!.GetValue<int>().ShouldBe(2000);
        // Overhead = latency − provider time: 100, 50, 20 and 10 ms.
        totals["overhead"]!["avg"]!.GetValue<double>().ShouldBe(45, 1e-9);
        totals["overhead"]!["p50"]!.GetValue<int>().ShouldBe(20);
        totals["overhead"]!["p99"]!.GetValue<int>().ShouldBe(100);
        totals["firstToken"]!["avg"]!.GetValue<double>().ShouldBe(300, 1e-9);
        // Successful requests with output: 400 tokens in 2850 ms of provider time.
        totals["tokensPerSecond"]!.GetValue<double>().ShouldBe(400 * 1000d / 2850, 1e-9);

        var buckets = metrics["buckets"]!.AsArray();
        buckets.Select(b => b!["start"]!.GetValue<DateTimeOffset>()).ShouldBe([first, second, second.AddHours(1)]); // no gaps
        buckets.Select(b => b!["requests"]!.GetValue<long>()).ShouldBe([3L, 3L, 0L]);
        buckets[0]!["latency"]!["p50"]!.GetValue<int>().ShouldBe(1000);
        buckets[0]!["firstTokenAvgMs"]!.GetValue<double>().ShouldBe(300, 1e-9);
        buckets[1]!["latency"]!["avg"]!.GetValue<double>().ShouldBe(100, 1e-9);
        buckets[2]!["latency"]!["avg"].ShouldBeNull();

        var models = metrics["models"]!.AsArray();
        models.Select(m => m!["model"]?.GetValue<string>()).ShouldBe(["m-a", "m-b", "m-live"]);
        models[0]!["requestsPerBucket"]!.AsArray().Select(v => v!.GetValue<long>()).ShouldBe([2L, 1L, 0L]);
        models[0]!["costUsdPerBucket"]!.AsArray().Select(v => v!.GetValue<decimal>()).ShouldBe([0.30m, 0m, 0m]);

        // Filters.
        (await GetAsync(viewer, $"{window}&model=m-b"))["totals"]!["requests"]!.GetValue<long>().ShouldBe(2);
        (await GetAsync(viewer, $"{window}&outcome=Success"))["totals"]!["requests"]!.GetValue<long>().ShouldBe(3);
        var sessions = await GetAsync(viewer, $"{window}&endpoint=Realtime");
        sessions["totals"]!["requests"]!.GetValue<long>().ShouldBe(1);
        sessions["totals"]!["latency"]!["avg"].ShouldBeNull();
        (await GetAsync(viewer, $"{window}&provider=nobody"))["totals"]!["requests"]!.GetValue<long>().ShouldBe(0);

        // Another department's usage is invisible, also when asked for by department.
        (await GetAsync(viewer, $"{window}&departmentId={other.Department}"))["totals"]!["requests"]!.GetValue<long>().ShouldBe(0);
        using var outsider = await fixture.ClientAsync("viewer", other.Code);
        (await GetAsync(outsider, window))["totals"]!["costUsd"]!.GetValue<decimal>().ShouldBe(9m);
        (await GetAsync(admin, $"{window}&departmentId={own.Department}"))["totals"]!["requests"]!.GetValue<long>().ShouldBe(6);
    }

    [Fact]
    public async Task Long_periods_are_bucketed_by_day()
    {
        using var client = await fixture.ClientAsync();
        var to = DateTimeOffset.UtcNow;
        var metrics = await GetAsync(client, $"from={Iso(to.AddDays(-30))}&to={Iso(to)}");
        metrics["bucket"]!.GetValue<string>().ShouldBe("day");
        metrics["timeZone"]!.GetValue<string>().ShouldBe("Europe/Stockholm");
        metrics["buckets"]!.AsArray().Count.ShouldBeInRange(30, 31);
    }

    [Theory]
    [InlineData("bucket=week")]
    [InlineData("bucket=hour&from=2026-01-01T00:00:00Z&to=2026-01-09T00:00:00Z")]
    [InlineData("from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    public async Task Invalid_queries_are_rejected(string query)
    {
        using var client = await fixture.ClientAsync();
        using var response = await client.GetAsync("/api/usage/metrics?" + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Day_slots_follow_Stockholm_midnight_across_the_dst_change()
    {
        // Summer time ends on 25 October 2026: that day is 25 hours long.
        var from = new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero); // 25 Oct 00:00 CEST
        var to = new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero); // 27 Oct 00:00 CET
        UsageMetricsService.Slots(from, to, MetricsBucket.Day).ShouldBe(
            [from, new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero)]);
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private static async Task<JsonNode> GetAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/usage/metrics?" + query, Ct);
        return await AdminFixture.ReadAsync(response);
    }

    private static UsageRecord Record((Guid Department, Guid Team, string Code) owner, DateTimeOffset at, string model, RequestOutcome outcome, Guid? provider,
        int latency, int? upstream = null, int? firstToken = null, long input = 0, long cached = 0, long output = 0, decimal cost = 0, int fallbacks = 0,
        GatewayEndpoint endpoint = GatewayEndpoint.ChatCompletions) => new()
        {
            RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = at, VirtualKeyId = Guid.NewGuid(), TeamId = owner.Team, DepartmentId = owner.Department,
            Endpoint = endpoint, RequestedModel = model, ProviderAccountId = provider, ProviderName = provider is null ? null : "metrics-test",
            Outcome = outcome, StatusCode = outcome == RequestOutcome.Success ? 200 : 502, LatencyMs = latency, UpstreamMs = upstream,
            TimeToFirstTokenMs = firstToken, Streamed = firstToken is not null, InputTokens = input, CachedInputTokens = cached, OutputTokens = output,
            CostUsd = cost, CostSek = cost * 10, FallbackCount = fallbacks,
        };

    private async Task SaveAsync(List<UsageRecord> records)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.UsageRecords.AddRange(records);
        await db.SaveChangesAsync(Ct);
    }
}
