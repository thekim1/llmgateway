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
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Domain.Usage;
using Ume.LlmGateway.Gateway.Authentication;
using Ume.LlmGateway.Gateway.Budgets;
using Ume.LlmGateway.Gateway.Requests;
using Ume.LlmGateway.Gateway.Routing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Stores;
using Ume.LlmGateway.TestKit;

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
    [InlineData("""{"choices":[{"delta":{"content":"x"}}],"usage":null}""", false)]
    [InlineData("""{"choices":[{"delta":{"content":"x"}}],"usage" : null,"obfuscation":"ab"}""", false)]
    [InlineData("""{"choices":[{"delta":{"content":"say \"usage\": 1"}}],"usage":null}""", false)]
    [InlineData("""{"type":"response.created","response":{"id":"r","usage":null}}""", false)]
    [InlineData("""{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}""", true)]
    [InlineData("""{"choices":[],"usage": {"prompt_tokens":7}}""", true)]
    [InlineData("""{"type":"response.completed","response":{"usage":null,"x":{"usage":{"input_tokens":1}}}}""", true)]
    [InlineData("""{"choices":[]}""", false)]
    public void Usage_value_detection_ignores_null_usage(string data, bool expected) =>
        ProviderJson.HasUsageValue(data).ShouldBe(expected);

    [Fact]
    public void Openai_wire_chunks_with_null_usage_take_the_fast_path_and_still_count_text()
    {
        // OpenAI and Azure send "usage":null in every chunk once include_usage is set (the gateway always sets it).
        var data = """{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hej "},"finish_reason":null}],"usage":null,"obfuscation":"q7"}""";
        var acc = new UsageAccumulator();
        OpenAICompatibleAdapter.TransformChunk(new SseEvent(null, data), acc, clientWantsUsage: false).ToList().ShouldHaveSingleItem().Data.ShouldBe(data);
        acc.OutputCharacters.ShouldBe(4);
        acc.Reported.ShouldBeFalse();
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    public async Task Compressed_streams_are_decoded_before_the_sse_reader(string encoding)
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hej\"}}]}\n\ndata: [DONE]\n\n"u8.ToArray();
        using var compressed = new MemoryStream();
        await using (Stream z = encoding switch
        {
            "gzip" => new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true),
            "br" => new System.IO.Compression.BrotliStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true),
            _ => new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true),
        })
        {
            await z.WriteAsync(sse, Ct);
        }

        compressed.Position = 0;
        var events = new List<SseEvent>();
        await foreach (var evt in SseReader.ReadAsync(ProviderAdapterBase.Decoded(compressed, [encoding]), Ct))
        {
            events.Add(evt);
        }

        events.Select(e => e.Data).ShouldBe(["""{"choices":[{"delta":{"content":"Hej"}}]}""", SseEvent.Done]);
    }

    [Fact]
    public void Stream_gaps_keep_the_longest_waits_and_count_stalls()
    {
        var time = new FakeTimeProvider();
        var gaps = new StreamGaps(time);
        var second = time.TimestampFrequency;
        gaps.Upstream(second / 100);
        gaps.Write(second / 1000);
        gaps.Upstream(second * 3);
        gaps.Write(second / 500);
        gaps.Upstream(second);
        gaps.Write(1);

        gaps.Events.ShouldBe(3);
        gaps.Stalls.ShouldBe(2);
        gaps.MaxUpstream.ShouldBe(TimeSpan.FromSeconds(3));
        gaps.MaxWrite.ShouldBe(TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public async Task Streamed_calls_do_not_ask_for_compression()
    {
        using var http = new ProviderHttpClient();
        (await RawRequestHeadersAsync(http.StreamClient)).ShouldNotContain("accept-encoding", Case.Insensitive);
        (await RawRequestHeadersAsync(http.Client)).ShouldContain("accept-encoding", Case.Insensitive);
    }

    /// <summary>The request head a client puts on the wire, read by a one-shot loopback listener.</summary>
    private static async Task<string> RawRequestHeadersAsync(HttpClient client)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var send = client.GetAsync(new Uri($"http://127.0.0.1:{port}/v1/models"), Ct);
            using var socket = await listener.AcceptTcpClientAsync(Ct);
            var stream = socket.GetStream();
            var buffer = new byte[8192];
            var head = new StringBuilder();
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, Ct);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), Ct);
            using var response = await send;
            return head.ToString();
        }
        finally
        {
            listener.Stop();
        }
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
            db.UsageRecords.Add(new UsageRecord { RequestId = "seed-" + teamId, Timestamp = DateTimeOffset.UtcNow, TeamId = teamId, RequestedModel = "m", CostUsd = 4m });
            await db.SaveChangesAsync(Ct);
        }

        Budget[] budget = [new() { Scope = BudgetScope.Team, ScopeId = teamId, LimitUsd = 10, Period = BudgetPeriod.Monthly }];
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => budgets.ReserveAsync(budget, 0.5m, Ct)));

        // 4 SEK already spent: 10 - 4 = 6 SEK allows 12 reservations of 0.5, so all 8 pass and none is double-seeded.
        results.ShouldAllBe(r => r.Allowed);
        results.Max(r => r.RemainingUsd).ShouldBe(6m);
        var key = results[0].Checks[0].Counter.Key;
        (await ledger.GetAsync([key], Ct))[0].ShouldBe(BudgetService.ToMicro(4m + (8 * 0.5m)));
    }

    [Fact]
    public void A_model_name_two_providers_share_needs_the_provider_and_never_picks_one()
    {
        var foundry = new ProviderAccount { Name = "foundry", BaseUrl = "https://f" };
        var ollama = new ProviderAccount { Name = "ollama", BaseUrl = "https://o" };
        var shared = new ModelDeployment { Id = Guid.CreateVersion7(), Name = "DeepSeek-V3", UpstreamModel = "DeepSeek-V3", ProviderAccount = foundry };
        var sameName = new ModelDeployment { Id = Guid.CreateVersion7(), Name = "deepseek-v3", UpstreamModel = "deepseek-v3", ProviderAccount = ollama };
        var own = new ModelDeployment { Id = Guid.CreateVersion7(), Name = "gpt-4.1", UpstreamModel = "gpt-4.1", ProviderAccount = foundry };
        foundry.Deployments.AddRange([shared, own]);
        ollama.Deployments.Add(sameName);

        var snapshot = new CatalogSnapshot([foundry, ollama], [], [], DateTimeOffset.UtcNow);

        snapshot.Resolve("deepseek-v3").ShouldBeNull();
        snapshot.AmbiguousChoices("DEEPSEEK-V3").ShouldBe(["foundry/DeepSeek-V3", "ollama/deepseek-v3"]);
        snapshot.Resolve("ollama/DeepSeek-V3")!.Targets.Single().ModelDeployment.ShouldBeSameAs(sameName);
        snapshot.Resolve("foundry/deepseek-v3")!.Name.ShouldBe("foundry/DeepSeek-V3");
        snapshot.Resolve("gpt-4.1")!.Name.ShouldBe("gpt-4.1");
        snapshot.Resolve("foundry/gpt-4.1")!.Name.ShouldBe("gpt-4.1"); // provider/name always works
        snapshot.DuplicateNames.ShouldBeEmpty();

        var key = new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h", AllowedModels = ["DeepSeek-V3"] };
        snapshot.Resolve("ollama/deepseek-v3")!.IsAllowedFor(key).ShouldBeTrue(); // the plain name covers each provider's model
        snapshot.Resolve("gpt-4.1")!.IsAllowedFor(key).ShouldBeFalse();

        var route = new RouteAlias { Name = "deepseek-v3", Kind = ModelKind.Chat, Targets = [new RouteTarget { ModelDeployment = sameName, ModelDeploymentId = sameName.Id }] };
        var routed = new CatalogSnapshot([foundry, ollama], [route], [], DateTimeOffset.UtcNow);
        routed.Resolve("DeepSeek-V3")!.Targets.Single().ModelDeployment.ShouldBeSameAs(sameName); // an admin's route decides
        routed.AmbiguousChoices("DeepSeek-V3").ShouldBeNull();

        var rejection = new RouteResolver(null!).ResolveModel(snapshot, new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h" }, GatewayEndpoint.ChatCompletions, "deepseek-v3").Rejection!;
        rejection.StatusCode.ShouldBe(400);
        rejection.Code.ShouldBe(GatewayErrorCodes.ModelAmbiguous);
        rejection.Message.ShouldContain("foundry/DeepSeek-V3, ollama/deepseek-v3");
    }
    [Fact]
    public void Key_lineage_follows_rotations_backwards()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), other = Guid.NewGuid();
        var snapshot = new CatalogSnapshot([], [], [], DateTimeOffset.UtcNow, new Dictionary<Guid, Guid> { [a] = b, [b] = c });
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
        // A deadline, not a count: the refresh is a database query, slow on a busy two-CPU CI runner.
        for (var deadline = DateTime.UtcNow.AddSeconds(10); ReferenceEquals(refreshed, first) && DateTime.UtcNow < deadline;)
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
    [Trait(TestCategory.Name, TestCategory.Flaky)] // failed in CI (2026-10-15): races the background key refresh
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
        for (var deadline = DateTime.UtcNow.AddSeconds(10); enabled && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(10, Ct);
            enabled = (await keys.FindAsync(key.Secret, Ct))!.IsEnabled;
        }

        enabled.ShouldBeFalse();

        await SetEnabledAsync(key.Id, true);
        await bus.PublishAsync(InvalidationKind.Keys, Ct);
        (await keys.FindAsync(key.Secret, Ct))!.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_lookup_running_before_an_invalidation_is_not_shared_after_it()
    {
        var key = await fixture.CreateKeyAsync();
        var bus = new InMemoryInvalidationBus();
        using var held = new SemaphoreSlim(0);
        var scopes = new HoldFirstScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), held);
        using var keys = new KeyAuthenticator(fixture.Services.GetRequiredService<VirtualKeyHasher>(), scopes,
            new StaticOptions(new GatewayOptions { KeyCacheSeconds = 30 }), bus, TimeProvider.System);

        // The first lookup starts while the key is enabled and is held before it finishes.
        var before = keys.FindAsync(key.Secret, Ct);
        await scopes.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The key is disabled and the change published while that lookup is still running.
        await SetEnabledAsync(key.Id, false);
        await bus.PublishAsync(InvalidationKind.Keys, Ct);

        // A lookup after the invalidation must not wait for (and return) the old one.
        (await keys.FindAsync(key.Secret, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct))!.IsEnabled.ShouldBeFalse();

        held.Release();
        await before;
    }

    /// <summary>Holds the first scope (the first database lookup) until released; later scopes go straight through.</summary>
    private sealed class HoldFirstScopeFactory(IServiceScopeFactory inner, SemaphoreSlim held) : IServiceScopeFactory
    {
        private int _created;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _created) == 1)
            {
                FirstStarted.SetResult();
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return inner.CreateScope();
        }
    }

    [Fact]
    public async Task Unknown_key_is_remembered_until_it_expires_or_a_key_is_created()
    {
        var hasher = fixture.Services.GetRequiredService<VirtualKeyHasher>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bus = new InMemoryInvalidationBus();
        using var keys = new KeyAuthenticator(hasher, fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions(new GatewayOptions { KeyCacheSeconds = 30 }), bus, time);

        // Expiry: the key appears in the database without an invalidation (e.g. a lost pub/sub message).
        var late = hasher.Generate();
        (await keys.FindAsync(late.PlainText, Ct)).ShouldBeNull();
        await InsertKeyAsync(late);
        (await keys.FindAsync(late.PlainText, Ct)).ShouldBeNull(); // remembered as unknown: no query
        time.Advance(TimeSpan.FromSeconds(31));
        (await keys.FindAsync(late.PlainText, Ct)).ShouldNotBeNull();

        // Creation: the admin API publishes a key invalidation, which works immediately.
        var created = hasher.Generate();
        (await keys.FindAsync(created.PlainText, Ct)).ShouldBeNull();
        await InsertKeyAsync(created);
        await bus.PublishAsync(InvalidationKind.Keys, Ct);
        (await keys.FindAsync(created.PlainText, Ct)).ShouldNotBeNull();
    }

    private async Task InsertKeyAsync(GeneratedKey generated)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var team = new Team { Name = Guid.NewGuid().ToString(), DepartmentId = fixture.DepartmentId, CreatedAt = DateTimeOffset.UtcNow };
        db.VirtualKeys.Add(new VirtualKey { Team = team, Name = "Late key", Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
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
