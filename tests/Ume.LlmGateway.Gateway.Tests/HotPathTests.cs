using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>The fast paths on the request/stream hot path must give exactly the results of the straightforward code they replace.</summary>
public sealed class HotPathTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("""{"choices":[{"index":0,"delta":{"content":"Hej "}}]}""")]
    [InlineData("""{"choices":[{"index":0,"delta":{"content":"rad 1\nrad 2 \"citat\" åäö 😀"}}]}""")]
    [InlineData("""{"choices":[{"index":0,"delta":{"content":"a"}},{"index":1,"delta":{"content":"bc"}}]}""")]
    [InlineData("""{"choices":[{"index":0,"delta":{"role":"assistant","content":""},"logprobs":null}]}""")]
    [InlineData("""{"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":"{\"content\":\"x\"}"}}]}}]}""")]
    [InlineData("""{"choices":[],"other":{"delta":{"content":"not counted"}}}""")]
    [InlineData("""{"type":"response.output_text.delta","delta":"Svar på frågan"}""")]
    [InlineData("""{"type":"response.created","response":{"delta":"nested, not counted"}}""")]
    [InlineData("""not json""")]
    public void Openai_chunk_text_length_matches_a_full_parse(string data)
    {
        ProviderJson.OpenAIContentLength(data).ShouldBe(ReferenceOpenAILength(data));

        var acc = new UsageAccumulator();
        OpenAICompatibleAdapter.TransformChunk(new SseEvent(null, data), acc, clientWantsUsage: false).ToList().ShouldHaveSingleItem();
        acc.OutputCharacters.ShouldBe(ReferenceOpenAILength(data));
    }

    [Fact]
    public void Openai_chunk_text_length_handles_long_escaped_content()
    {
        var text = string.Concat(Enumerable.Repeat("rad\n\"å\"\t", 400));
        var data = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) }.ToJsonString();
        ProviderJson.OpenAIContentLength(data).ShouldBe(text.Length);
    }

    [Fact]
    public void Usage_chunks_still_report_usage_and_are_hidden_unless_requested()
    {
        var usage = """{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}""";
        var acc = new UsageAccumulator();
        OpenAICompatibleAdapter.TransformChunk(new SseEvent(null, usage), acc, clientWantsUsage: false).ShouldBeEmpty();
        acc.Reported.ShouldBeTrue();
        acc.ToTokenUsage(0).ShouldBe(new TokenUsage(7, 0, 3));
    }

    [Theory]
    [InlineData("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ord \"x\""}}""", 7)]
    [InlineData("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}""", 0)]
    [InlineData("ping", """{"type":"ping"}""", 0)]
    public void Anthropic_passthrough_counts_text_by_event_name(string name, string data, long expected)
    {
        var acc = new UsageAccumulator();
        AnthropicAdapter.Passthrough(new SseEvent(name, data), acc).ShouldHaveSingleItem();
        acc.OutputCharacters.ShouldBe(expected);
    }

    [Fact]
    public void Anthropic_passthrough_still_reads_usage_events()
    {
        var acc = new UsageAccumulator();
        AnthropicAdapter.Passthrough(new SseEvent("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":12,"output_tokens":1}}}"""), acc);
        AnthropicAdapter.Passthrough(new SseEvent("message_delta", """{"type":"message_delta","usage":{"output_tokens":40}}"""), acc);
        acc.ToTokenUsage(0).ShouldBe(new TokenUsage(12, 0, 40));
    }

    [Theory]
    [InlineData("""{"id":"x","data":[{"embedding":[0.1,-0.2]}],"usage":{"prompt_tokens":5,"total_tokens":5}}""", 5, 0, 0)]
    [InlineData("""{"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":10}},"choices":[]}""", 100, 10, 20)]
    [InlineData("""{"content":[{"type":"text","text":"usage"}],"usage":{"input_tokens":3,"output_tokens":4}}""", 3, 0, 4)]
    [InlineData("""{"choices":[{"message":{"usage":{"prompt_tokens":9}}}]}""", 0, 0, 0)]
    [InlineData("""[1,2,3]""", 0, 0, 0)]
    public void Json_usage_is_read_without_parsing_the_whole_answer(string json, long input, long cached, long output)
    {
        ProviderJson.ReadUsage(Encoding.UTF8.GetBytes(json)).ShouldBe(new TokenUsage(input, cached, output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"usage":{"prompt_tokens":1}""")]
    [InlineData("""{"a":1} trailing""")]
    [InlineData("<html>Bad gateway</html>")]
    public void Json_usage_rejects_invalid_json(string json)
    {
        Should.Throw<JsonException>(() => ProviderJson.ReadUsage(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(null, "{\"a\":1}")]
    [InlineData("delta", "first\nsecond")]
    [InlineData(null, "")]
    [InlineData(null, "trailing\n")]
    [InlineData("x", "å ä ö 😀")]
    public void Sse_events_are_written_in_wire_format(string? name, string data)
    {
        var expected = (name is null ? "" : $"event: {name}\n") + string.Concat(data.Split('\n').Select(l => $"data: {l}\n")) + "\n";
        var writer = new ArrayBufferWriter<byte>();
        new SseEvent(name, data).WriteTo(writer);
        Encoding.UTF8.GetString(writer.WrittenSpan).ShouldBe(expected);
    }

    [Fact]
    public async Task Ledger_reports_missing_counters_without_reserving()
    {
        var ledger = new InMemorySpendLedger();
        SpendCounter[] counters = [new("a", 100, TimeSpan.FromMinutes(1)), new("b", 100, TimeSpan.FromMinutes(1))];
        await ledger.InitializeAsync("a", 10, TimeSpan.FromMinutes(1), Ct);

        var missing = await ledger.ReserveAsync(counters, 5, missingAsZero: false, Ct);
        missing.Missing.ShouldBe([1]);
        missing.Reserved.ShouldBeFalse();
        (await ledger.GetAsync(["a", "b"], Ct)).ShouldBe([10, null]);

        var reserved = await ledger.ReserveAsync(counters, 5, missingAsZero: true, Ct);
        reserved.Reserved.ShouldBeTrue();
        reserved.ValuesBefore.ShouldBe([10, 0]);
        (await ledger.GetAsync(["a", "b"], Ct)).ShouldBe([15, 5]);

        var exhausted = await ledger.ReserveAsync(counters, 90, missingAsZero: false, Ct);
        exhausted.ExhaustedIndex.ShouldBe(0);
        exhausted.ValuesBefore.ShouldBe([15, 5]);
        (await ledger.GetAsync(["a", "b"], Ct)).ShouldBe([15, 5]);
    }

    [Fact]
    public async Task Budget_reservation_seeds_cold_counters_from_usage_once()
    {
        var ledger = new InMemorySpendLedger();
        var budgets = new BudgetService(ledger, fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var teamId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.Add(new UsageRecord { RequestId = "seed-" + teamId, Timestamp = DateTimeOffset.UtcNow, TeamId = teamId, RequestedModel = "m", CostSek = 4m });
            await db.SaveChangesAsync(Ct);
        }

        Budget[] budget = [new() { Scope = BudgetScope.Team, ScopeId = teamId, LimitSek = 10, Period = BudgetPeriod.Monthly }];
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => budgets.ReserveAsync(budget, 0.5m, Ct)));

        // 4 SEK already spent: 10 - 4 = 6 SEK allows 12 reservations of 0.5, so all 8 pass and none is double-seeded.
        results.ShouldAllBe(r => r.Allowed);
        results.Max(r => r.RemainingSek).ShouldBe(6m);
        var key = results[0].Checks[0].Counter.Key;
        (await ledger.GetAsync([key], Ct))[0].ShouldBe(BudgetService.ToMicro(4m + (8 * 0.5m)));
    }

    [Fact]
    public void Key_lineage_follows_rotations_backwards()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), other = Guid.NewGuid();
        var snapshot = new CatalogSnapshot([], [], [], 10m, DateTimeOffset.UtcNow, new Dictionary<Guid, Guid> { [a] = b, [b] = c });
        snapshot.KeyLineage(c).ShouldBe([c, b, a], ignoreOrder: true);
        snapshot.KeyLineage(b).ShouldBe([b, a], ignoreOrder: true);
        snapshot.KeyLineage(other).ShouldBe([other]);
    }

    [Fact]
    public async Task Expired_catalog_is_served_while_it_refreshes_but_invalidation_reloads_at_once()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bus = new InMemoryInvalidationBus();
        using var catalog = new GatewayCatalog(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions(new GatewayOptions { CatalogCacheSeconds = 30 }), time, bus, NullLogger<GatewayCatalog>.Instance);

        var first = await catalog.GetAsync(Ct);
        (await catalog.GetAsync(Ct)).ShouldBeSameAs(first);

        time.Advance(TimeSpan.FromSeconds(31));
        (await catalog.GetAsync(Ct)).ShouldBeSameAs(first); // stale, refresh started in the background
        var refreshed = first;
        for (var i = 0; i < 200 && ReferenceEquals(refreshed, first); i++)
        {
            await Task.Delay(10, Ct);
            refreshed = await catalog.GetAsync(Ct);
        }

        refreshed.ShouldNotBeSameAs(first);
        refreshed.LoadedAt.ShouldBe(time.GetUtcNow());

        await bus.PublishAsync(InvalidationKind.Config, Ct);
        var reloaded = await catalog.GetAsync(Ct);
        reloaded.ShouldNotBeSameAs(refreshed);
    }

    [Fact]
    public async Task Expired_key_is_served_while_it_refreshes_and_revocation_drops_it()
    {
        var key = await fixture.CreateKeyAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bus = new InMemoryInvalidationBus();
        using var keys = new KeyAuthenticator(fixture.Services.GetRequiredService<VirtualKeyHasher>(), fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions(new GatewayOptions { KeyCacheSeconds = 30 }), bus, time);

        (await keys.FindAsync(key.Secret, Ct))!.IsEnabled.ShouldBeTrue();
        await SetEnabledAsync(key.Id, false);
        (await keys.FindAsync(key.Secret, Ct))!.IsEnabled.ShouldBeTrue(); // cached

        time.Advance(TimeSpan.FromSeconds(31));
        (await keys.FindAsync(key.Secret, Ct))!.IsEnabled.ShouldBeTrue(); // stale, refreshing in the background
        var enabled = true;
        for (var i = 0; i < 200 && enabled; i++)
        {
            await Task.Delay(10, Ct);
            enabled = (await keys.FindAsync(key.Secret, Ct))!.IsEnabled;
        }

        enabled.ShouldBeFalse();

        await SetEnabledAsync(key.Id, true);
        await bus.PublishAsync(InvalidationKind.Keys, Ct);
        (await keys.FindAsync(key.Secret, Ct))!.IsEnabled.ShouldBeTrue();
    }

    private async Task SetEnabledAsync(Guid keyId, bool enabled)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().VirtualKeys.Where(k => k.Id == keyId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.IsEnabled, enabled), Ct);
    }

    /// <summary>The DOM-based counting this replaced, kept as the reference.</summary>
    private static long ReferenceOpenAILength(string data)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return 0;
        }

        long length = 0;
        if (node is JsonObject obj)
        {
            if (obj["choices"] is JsonArray choices)
            {
                foreach (var choice in choices)
                {
                    if (choice?["delta"]?["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                    {
                        length += text.Length;
                    }
                }
            }
            else if (obj["delta"] is JsonValue delta && delta.TryGetValue<string>(out var responseDelta))
            {
                length += responseDelta.Length;
            }
        }

        return length;
    }

    private sealed class StaticOptions(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
