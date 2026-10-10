using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.IntegrationTests;

public sealed class IntegrationTests(IntegrationFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_key_gateway_accounting_budget_and_rotation_share_real_state()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using var gateway = fixture.GatewayClient(key.Secret);
        using var response = await CallAsync(gateway);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = response.Headers.GetValues("x-request-id").Single();
        await fixture.FlushAsync();
        using var usage = await admin.GetAsync("/api/usage/requests/" + requestId, Cancellation);
        var record = await IntegrationFixture.ReadAsync(usage);
        record["outcome"]!.GetValue<string>().ShouldBe("Success");
        record["costSek"]!.GetValue<decimal>().ShouldBeGreaterThan(0);
        using var budget = await admin.PostAsJsonAsync("/api/budgets", new
        {
            scope = "VirtualKey", scopeId = key.Id, limitSek = 0, period = "Monthly",
            alertThresholds = new[] { 80, 100 }, isActive = true,
        }, Cancellation);
        budget.EnsureSuccessStatusCode();
        await WaitForStatusAsync(gateway, HttpStatusCode.PaymentRequired);
        using var rotate = await admin.PostAsJsonAsync($"/api/keys/{key.Id}/rotate", new { mode = "Grace24Hours" }, Cancellation);
        var rotated = await IntegrationFixture.ReadAsync(rotate);
        using var replacement = fixture.GatewayClient(rotated["secret"]!.GetValue<string>());
        using var blocked = await CallAsync(replacement);
        blocked.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task Revocation_invalidates_a_warm_gateway_cache_across_redis()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using var gateway = fixture.GatewayClient(key.Secret);
        using var warm = await CallAsync(gateway);
        warm.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var revoke = await admin.PostAsync($"/api/keys/{key.Id}/revoke", null, Cancellation);
        revoke.EnsureSuccessStatusCode();
        await WaitForStatusAsync(gateway, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Redis_enforces_cross_request_rate_limits()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin, requestsPerMinute: 1);
        using var firstClient = fixture.GatewayClient(key.Secret);
        using var secondClient = fixture.GatewayClient(key.Secret);
        using var first = await CallAsync(firstClient);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await CallAsync(secondClient);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Seeded_transcription_alias_reaches_the_fake_whisper_and_is_billed_per_minute()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using var gateway = fixture.GatewayClient(key.Secret);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("ume/transcribe"), "model" },
            { new StringContent("verbose_json"), "response_format" },
            { new ByteArrayContent(new byte[160_000]), "file", "möte.mp3" }, // FakeLlm treats it as 10 s at 128 kbps
        };
        using var response = await gateway.PostAsync("/v1/audio/transcriptions", form, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-model").Single().ShouldBe("fake-eu/whisper-eu");
        var body = await IntegrationFixture.ReadAsync(response);
        body["text"]!.GetValue<string>().ShouldContain("möte.mp3");
        body["duration"]!.GetValue<double>().ShouldBe(10);
        await fixture.FlushAsync();
        using var scope = fixture.GatewayServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var usage = await db.UsageRecords.SingleAsync(r => r.VirtualKeyId == key.Id, Cancellation);
        usage.Endpoint.ShouldBe(Domain.GatewayEndpoint.AudioTranscriptions);
        usage.AudioSeconds.ShouldBe(10m); // verbose_json duration
        usage.CostUsd.ShouldBe(0.001m); // 10 s at 0.006 USD per minute
    }

    [Fact]
    public async Task Seeded_live_transcription_session_reaches_the_fake_provider_with_redis_session_slots()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using var socket = await fixture.GatewayRealtimeAsync(key.Secret, "ume/live-transcribe");

        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("session.created");
        var chunk = Convert.ToBase64String(new byte[4800]); // 100 ms of PCM16 at 24 kHz
        for (var i = 0; i < 20; i++)
        {
            await SendAsync(socket, new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = chunk });
        }

        await SendAsync(socket, new JsonObject { ["type"] = "input_audio_buffer.commit" });
        JsonObject completed;
        do
        {
            completed = await ReceiveAsync(socket);
        }
        while (completed["type"]!.GetValue<string>() != "conversation.item.input_audio_transcription.completed");

        completed["transcript"]!.GetValue<string>().ShouldContain("fake-whisper");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Cancellation);
        while ((await socket.ReceiveAsync(new byte[16 * 1024], Cancellation)).MessageType != WebSocketMessageType.Close)
        {
        }

        UsageRecord? usage = null;
        for (var attempt = 0; usage is null && attempt < 50; attempt++)
        {
            await fixture.FlushAsync();
            using var scope = fixture.GatewayServices.CreateScope();
            usage = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.SingleOrDefaultAsync(r => r.VirtualKeyId == key.Id, Cancellation);
            if (usage is null)
            {
                await Task.Delay(100, Cancellation);
            }
        }

        usage.ShouldNotBeNull();
        usage.Endpoint.ShouldBe(Domain.GatewayEndpoint.Realtime);
        usage.UpstreamModel.ShouldBe("fake-whisper");
        usage.AudioSeconds.ShouldBe(2m);
        usage.CostUsd.ShouldBe(0.0002m); // 2 s at 0.006 USD per minute
    }

    private static Task SendAsync(WebSocket socket, JsonObject evt) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt), WebSocketMessageType.Text, true, Cancellation);

    private static async Task<JsonObject> ReceiveAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var length = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
            length += result.Count;
        }
        while (!result.EndOfMessage);

        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, length))!;
    }

    [Fact]
    public async Task Fake_provider_fallback_and_pii_rerouting_run_through_the_real_pipeline()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using var gateway = fixture.GatewayClient(key.Secret);
        using var fallback = await CallAsync(gateway, "ume/demo-fallback");
        fallback.StatusCode.ShouldBe(HttpStatusCode.OK);
        fallback.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("1");
        using var pii = await CallAsync(gateway, content: "Synthetic identifier 19121212-1212");
        pii.StatusCode.ShouldBe(HttpStatusCode.OK);
        pii.Headers.GetValues("x-ume-provider").Single().ShouldBe("fake-onprem");
        pii.Headers.GetValues("x-ume-pii").Single().ShouldBe("rerouted-onprem");
        await fixture.FlushAsync();
        using var scope = fixture.GatewayServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        (await db.UsageRecords.CountAsync(r => r.VirtualKeyId == key.Id, Cancellation)).ShouldBe(2);
    }

    [Fact]
    public async Task Redis_requires_authentication()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(fixture.RedisHost, fixture.RedisPort, Cancellation);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n"), Cancellation);
        var buffer = new byte[256];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var count = await stream.ReadAsync(buffer, timeout.Token);
        Encoding.ASCII.GetString(buffer, 0, count).ShouldStartWith("-NOAUTH");
    }

    [Fact]
    public async Task Redis_budget_reservations_are_atomic_under_contention()
    {
        var ledger = fixture.GatewayServices.GetRequiredService<ISpendLedger>();
        ledger.ShouldBeOfType<RedisSpendLedger>();
        var counter = new SpendCounter("ume:integration:" + Guid.NewGuid().ToString("N"), 1000, TimeSpan.FromMinutes(1));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ledger.TryReserveAsync([counter], 100, Cancellation)));
        results.Count(r => r == -1).ShouldBe(10);
        (await ledger.GetAsync([counter.Key], Cancellation))[0].ShouldBe(1000);
    }

    [Fact]
    public async Task Redis_reports_token_usage_without_consuming_request_quota()
    {
        var limiter = fixture.GatewayServices.GetRequiredService<IRateLimiter>();
        limiter.ShouldBeOfType<RedisRateLimiter>();
        var key = Guid.NewGuid();
        (await limiter.PeekTokensUsedPercentAsync(key, null, Cancellation)).ShouldBeNull();
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Cancellation)).ShouldBe(0);
        await limiter.RecordTokensAsync(key, 300, Cancellation);
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Cancellation)).ShouldBe(30);
        await limiter.RecordTokensAsync(key, 5000, Cancellation);
        (await limiter.PeekTokensUsedPercentAsync(key, 1000, Cancellation)).ShouldBe(100);

        var other = Guid.NewGuid();
        await limiter.PeekTokensUsedPercentAsync(other, 1000, Cancellation);
        (await limiter.AcquireAsync(other, 1, null, Cancellation)).Allowed.ShouldBeTrue();
    }

    private static Task<HttpResponseMessage> CallAsync(HttpClient client, string model = "ume/chat-standard", string content = "Synthetic test request") =>
        client.PostAsJsonAsync("/v1/chat/completions", new { model, messages = new[] { new { role = "user", content } }, max_tokens = 32 }, Cancellation);

    private static async Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var response = await CallAsync(client);
            if (response.StatusCode == expected) { return; }
            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        }
        Assert.Fail($"Expected status {(int)expected} after cross-service invalidation.");
    }
}
