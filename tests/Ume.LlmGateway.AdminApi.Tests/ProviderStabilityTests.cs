using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Provider status on <c>/api/ops/health</c> and the stability series on <c>/api/ops/providers/stats</c>.</summary>
public sealed class ProviderStabilityTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, CircuitState.Closed, 100, 100, ProviderStatus.Disabled)]
    [InlineData(true, CircuitState.Open, 100, 0, ProviderStatus.CircuitOpen)]
    [InlineData(true, CircuitState.Closed, 0, 0, ProviderStatus.NoRecentTraffic)]
    [InlineData(true, CircuitState.Closed, 9, 9, ProviderStatus.Healthy)] // too few to judge
    [InlineData(true, CircuitState.Closed, 10, 0, ProviderStatus.Healthy)]
    [InlineData(true, CircuitState.Closed, 100, 4, ProviderStatus.Healthy)]
    [InlineData(true, CircuitState.Closed, 100, 5, ProviderStatus.Degraded)]
    [InlineData(true, CircuitState.Closed, 100, 49, ProviderStatus.Degraded)]
    [InlineData(true, CircuitState.Closed, 10, 5, ProviderStatus.Unhealthy)]
    public void Status_follows_recent_provider_errors(bool enabled, CircuitState circuit, long requests, long errors, ProviderStatus expected) =>
        ProviderStabilityService.Classify(enabled, circuit, requests, errors).Status.ShouldBe(expected);

    [Fact]
    public void Reason_says_how_many_requests_failed()
    {
        ProviderStabilityService.Classify(true, CircuitState.Closed, 40, 7).Reason.ShouldBe("7 of 40 requests failed at the provider in the last hour (17.5 %).");
        ProviderStabilityService.Classify(true, CircuitState.Closed, 1, 1).Reason.ShouldStartWith("1 of 1 request failed at the provider in the last hour; fewer than 10");
    }

    [Fact]
    public async Task Health_status_counts_only_recent_provider_errors()
    {
        using var client = await fixture.ClientAsync();
        var degraded = await ProviderAsync(client);
        var clientErrors = await ProviderAsync(client);
        var quiet = await ProviderAsync(client);
        var fewRequests = await ProviderAsync(client);
        var now = DateTimeOffset.UtcNow;

        var records = new List<UsageRecord>();
        // Last hour: 20 successes, 4 provider failures (502/504), and 16 failures that are not the provider's fault.
        records.AddRange(Records(degraded, now.AddMinutes(-10), 20, RequestOutcome.Success, 200));
        records.AddRange(Records(degraded, now.AddMinutes(-10), 3, RequestOutcome.ProviderError, 502, "stream_interrupted"));
        records.AddRange(Records(degraded, now.AddMinutes(-10), 1, RequestOutcome.ProviderError, 504));
        records.AddRange(Records(degraded, now.AddMinutes(-10), 10, RequestOutcome.ProviderError, 400, "provider_rejected"));
        records.AddRange(Records(degraded, now.AddMinutes(-10), 5, RequestOutcome.ClientCancelled, 499));
        records.AddRange(Records(degraded, now.AddMinutes(-10), 1, RequestOutcome.ProviderError, 500, "internal_error"));
        // A burst of provider failures three hours ago, then only client errors: no longer degraded.
        records.AddRange(Records(clientErrors, now.AddHours(-3), 20, RequestOutcome.ProviderError, 503));
        records.AddRange(Records(clientErrors, now.AddMinutes(-5), 12, RequestOutcome.ProviderError, 400, "provider_rejected"));
        records.AddRange(Records(fewRequests, now.AddMinutes(-5), 3, RequestOutcome.ProviderError, 502));
        await SaveAsync(records);

        using var response = await client.GetAsync("/api/ops/health", Ct);
        var providers = (await AdminFixture.ReadAsync(response))["providers"]!.AsArray();
        JsonNode Provider(Guid id) => providers.Single(p => p!["id"]!.GetValue<Guid>() == id)!;

        var d = Provider(degraded);
        d["status"]!.GetValue<string>().ShouldBe("Degraded");
        d["statusReason"]!.GetValue<string>().ShouldBe("4 of 40 requests failed at the provider in the last hour (10 %).");
        d["recentRequests"]!.GetValue<long>().ShouldBe(40);
        d["recentProviderErrors"]!.GetValue<long>().ShouldBe(4);
        d["requests24h"]!.GetValue<long>().ShouldBe(40);
        d["errorRate24h"]!.GetValue<double>().ShouldBe(0.5);
        d["providerErrorRate24h"]!.GetValue<double>().ShouldBe(0.1);

        var c = Provider(clientErrors);
        c["status"]!.GetValue<string>().ShouldBe("Healthy");
        c["errorRate24h"]!.GetValue<double>().ShouldBe(1);
        c["providerErrorRate24h"]!.GetValue<double>().ShouldBe(20 / 32d);

        Provider(fewRequests)["status"]!.GetValue<string>().ShouldBe("Healthy");
        Provider(quiet)["status"]!.GetValue<string>().ShouldBe("NoRecentTraffic");
        Provider(quiet)["statusReason"]!.GetValue<string>().ShouldBe("No requests in the last hour.");
    }

    [Fact]
    public async Task Stats_bucket_hours_in_utc_and_days_in_stockholm_and_rank_the_most_stable_first()
    {
        using var client = await fixture.ClientAsync();
        var stable = Guid.NewGuid();
        var flaky = Guid.NewGuid();
        // 2025-03-29 23:30 local is 22:30Z; 2025-03-30 00:30 local (still CET) is 23:30Z.
        var lateSaturday = new DateTimeOffset(2025, 3, 29, 22, 30, 0, TimeSpan.Zero);
        var earlySunday = new DateTimeOffset(2025, 3, 29, 23, 30, 0, TimeSpan.Zero);

        var records = new List<UsageRecord>();
        for (var i = 1; i <= 20; i++)
        {
            records.AddRange(Records(stable, lateSaturday, 1, RequestOutcome.Success, 200, latencyMs: i * 100));
        }
        records.AddRange(Records(stable, lateSaturday, 1, RequestOutcome.Success, 200, latencyMs: 3_600_000, endpoint: GatewayEndpoint.Realtime));
        records.AddRange(Records(flaky, earlySunday, 8, RequestOutcome.Success, 200, latencyMs: 50, fallbacks: 1));
        records.AddRange(Records(flaky, earlySunday, 2, RequestOutcome.ProviderError, 502, latencyMs: 50));
        records.AddRange(Records(flaky, earlySunday, 5, RequestOutcome.ClientCancelled, 499, latencyMs: 50));
        // MCP tool calls have no provider: failed ones must not count against any provider or show up as one.
        records.AddRange(Records(Guid.Empty, earlySunday, 6, RequestOutcome.ProviderError, 502, "mcp_upstream_failed", endpoint: GatewayEndpoint.Mcp)
            .Select(r => { r.ProviderAccountId = null; r.ProviderName = null; r.McpServerId = Guid.NewGuid(); r.McpMethod = "tools/call"; return r; }));
        await SaveAsync(records);

        var from = Uri.EscapeDataString("2025-03-29T12:00:00Z");
        var to = Uri.EscapeDataString("2025-03-30T12:00:00Z");
        using var hourly = await client.GetAsync($"/api/ops/providers/stats?from={from}&to={to}&bucket=Hour", Ct);
        var hours = await AdminFixture.ReadAsync(hourly);
        hours["bucket"]!.GetValue<string>().ShouldBe("Hour");
        var stableHour = hours["buckets"]!.AsArray().Single(b => b!["providerId"]!.GetValue<Guid>() == stable)!;
        stableHour["start"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2025, 3, 29, 22, 0, 0, TimeSpan.Zero));
        stableHour["requests"]!.GetValue<long>().ShouldBe(21);
        stableHour["p50LatencyMs"]!.GetValue<int>().ShouldBe(1000); // the realtime session's hour-long "latency" is left out
        stableHour["p95LatencyMs"]!.GetValue<int>().ShouldBe(1900);
        var flakyHour = hours["buckets"]!.AsArray().Single(b => b!["providerId"]!.GetValue<Guid>() == flaky)!;
        flakyHour["start"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2025, 3, 29, 23, 0, 0, TimeSpan.Zero));
        flakyHour["providerErrors"]!.GetValue<long>().ShouldBe(2);
        flakyHour["errorRate"]!.GetValue<double>().ShouldBe(2 / 15d);
        flakyHour["fallbacks"]!.GetValue<long>().ShouldBe(8);

        using var daily = await client.GetAsync($"/api/ops/providers/stats?from={from}&to={to}&bucket=day", Ct);
        var days = await AdminFixture.ReadAsync(daily);
        days["timeZone"]!.GetValue<string>().ShouldBe("Europe/Stockholm");
        days["from"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2025, 3, 28, 23, 0, 0, TimeSpan.Zero)); // Stockholm midnight
        days["buckets"]!.AsArray().Single(b => b!["providerId"]!.GetValue<Guid>() == stable)!["start"]!.GetValue<DateTimeOffset>()
            .ShouldBe(new DateTimeOffset(2025, 3, 28, 23, 0, 0, TimeSpan.Zero));
        days["buckets"]!.AsArray().Single(b => b!["providerId"]!.GetValue<Guid>() == flaky)!["start"]!.GetValue<DateTimeOffset>()
            .ShouldBe(new DateTimeOffset(2025, 3, 29, 23, 0, 0, TimeSpan.Zero));

        var ranking = days["providers"]!.AsArray().Where(p => p!["providerId"]!.GetValue<Guid>() is var id && (id == stable || id == flaky)).ToList();
        ranking.Select(p => p!["providerId"]!.GetValue<Guid>()).ShouldBe([stable, flaky]);
        ranking[0]!["availability"]!.GetValue<double>().ShouldBe(1);
        ranking[0]!["name"]!.GetValue<string>().ShouldBe("stability-test");
        ranking[0]!["exists"]!.GetValue<bool>().ShouldBeFalse();
        ranking[1]!["availability"]!.GetValue<double>().ShouldBe(1 - (2 / 15d));
        ranking[1]!["requests"]!.GetValue<long>().ShouldBe(15);
        days["providers"]!.AsArray().ShouldAllBe(p => p!["providerId"]!.GetValue<Guid>() != Guid.Empty);
        hours["buckets"]!.AsArray().ShouldAllBe(b => b!["providerId"]!.GetValue<Guid>() != Guid.Empty);
    }

    [Theory]
    [InlineData("from=2025-01-01T00:00:00Z&to=2025-01-09T00:00:00Z&bucket=hour", "Timvis indelning tillåter högst 7 dygn. Välj day för längre perioder.")]
    [InlineData("from=2025-01-01T00:00:00Z&to=2025-05-01T00:00:00Z&bucket=day", "Välj ett tidsintervall på högst 90 dygn.")]
    [InlineData("from=2025-01-02T00:00:00Z&to=2025-01-01T00:00:00Z", "Tidsintervallets slut måste ligga efter dess början.")]
    [InlineData("bucket=week", "Indelningen måste vara hour eller day.")]
    public async Task Stats_reject_invalid_ranges(string query, string message)
    {
        using var client = await fixture.ClientAsync();
        using var response = await client.GetAsync("/api/ops/providers/stats?" + query, Ct);
        (await AdminFixture.ReadAsync(response, HttpStatusCode.BadRequest))["detail"]!.GetValue<string>().ShouldBe(message);
    }

    [Fact]
    public async Task Stats_default_to_the_last_24_hours_and_need_the_admin_role()
    {
        using var admin = await fixture.ClientAsync();
        using var response = await admin.GetAsync("/api/ops/providers/stats", Ct);
        var body = await AdminFixture.ReadAsync(response);
        (body["to"]!.GetValue<DateTimeOffset>() - body["from"]!.GetValue<DateTimeOffset>()).ShouldBeInRange(TimeSpan.FromHours(24), TimeSpan.FromHours(25));

        using var viewer = await fixture.ClientAsync("viewer");
        using var forbidden = await viewer.GetAsync("/api/ops/providers/stats", Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<Guid> ProviderAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "stability-" + Guid.NewGuid().ToString("N")[..12], baseUrl = "http://localhost:12345/v1", type = "Ollama", authMode = "None",
            residency = "OnPrem", capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 10, isEnabled = true,
        }, Ct);
        return (await AdminFixture.ReadAsync(response, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    private static IEnumerable<UsageRecord> Records(Guid provider, DateTimeOffset at, int count, RequestOutcome outcome, int status,
        string? errorCode = null, int latencyMs = 100, GatewayEndpoint endpoint = GatewayEndpoint.ChatCompletions, int fallbacks = 0) =>
        Enumerable.Range(0, count).Select(_ => new UsageRecord
        {
            RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = at, VirtualKeyId = Guid.NewGuid(), TeamId = Guid.NewGuid(),
            DepartmentId = Guid.NewGuid(), Endpoint = endpoint, RequestedModel = "test/model", ProviderAccountId = provider,
            ProviderName = "stability-test", Outcome = outcome, StatusCode = status, ErrorCode = errorCode, LatencyMs = latencyMs,
            FallbackCount = fallbacks,
        });

    private async Task SaveAsync(List<UsageRecord> records)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.UsageRecords.AddRange(records);
        await db.SaveChangesAsync(Ct);
    }
}
