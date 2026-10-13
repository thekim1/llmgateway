extern alias Gateway;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;
using GatewayCatalog = Gateway::Ume.LlmGateway.Gateway.Pipeline.GatewayCatalog;

namespace Ume.LlmGateway.IntegrationTests;

public sealed class McpIntegrationTests(IntegrationFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeded_demo_mcp_server_matches_the_fake_server_and_reaches_the_catalogue()
    {
        using var scope = fixture.GatewayServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var server = await db.McpServers.AsNoTracking().Include(s => s.Tools).SingleAsync(s => s.Slug == "demo", Cancellation);
        server.Url.ShouldBe(fixture.FakeMcpUrl);
        server.IsEnabled.ShouldBeTrue();

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(server.Url), TransportMode = HttpTransportMode.StreamableHttp }),
            cancellationToken: Cancellation);
        var upstream = (await client.ListToolsAsync(cancellationToken: Cancellation)).Select(t => t.ProtocolTool).ToList();
        upstream.Select(t => t.Name).ShouldBe(server.Tools.Select(t => t.Name), ignoreOrder: true);
        foreach (var tool in upstream)
        {
            var offered = JsonSerializer.SerializeToNode(tool, McpJsonUtilities.DefaultOptions)!.AsObject();
            var definition = new JsonObject();
            foreach (var field in new[] { "description", "inputSchema", "outputSchema", "annotations" })
            {
                if (offered[field] is { } value)
                {
                    definition[field] = value.DeepClone();
                }
            }

            var seeded = server.Tools.Single(t => t.Name == tool.Name);
            JsonNode.DeepEquals(JsonNode.Parse(seeded.ApprovedDefinition!), definition).ShouldBeTrue(tool.Name);
        }

        var catalog = fixture.GatewayServices.GetRequiredService<GatewayCatalog>();
        catalog.Invalidate();
        var snapshot = await catalog.GetAsync(Cancellation);
        snapshot.McpServers["demo"].Id.ShouldBe(server.Id);
        snapshot.McpTools.Keys.Where(k => k.StartsWith("demo__", StringComparison.Ordinal))
            .ShouldBe(["demo__echo", "demo__get_time", "demo__get_image"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_seeded_demo_tool_is_called_through_the_gateway_with_its_image_and_accounted()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using (var scope = fixture.GatewayServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var demo = await db.McpServers.SingleAsync(s => s.Slug == "demo", Cancellation);
            db.VirtualKeyMcpGrants.Add(new VirtualKeyMcpGrant { VirtualKeyId = key.Id, McpServerId = demo.Id, AllowedTools = ["get_image", "echo"], AllowResources = true, AllowPrompts = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Cancellation);
        }

        // The grant bypasses the admin API, so nothing tells the gateway to reload its catalog.
        fixture.GatewayServices.GetRequiredService<GatewayCatalog>().Invalidate();

        using var gateway = fixture.GatewayClient(key.Secret);
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = "call-1", ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "demo__get_image", ["arguments"] = new JsonObject() },
        };
        using var response = await gateway.PostAsync("/mcp", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), Cancellation);
        var answer = await IntegrationFixture.ReadAsync(response);

        answer["id"]!.GetValue<string>().ShouldBe("call-1");
        var content = answer["result"]!["content"]!.AsArray();
        content[1]!["type"]!.GetValue<string>().ShouldBe("image");
        content[1]!["mimeType"]!.GetValue<string>().ShouldBe("image/png");
        await fixture.FlushAsync();
        using (var scope = fixture.GatewayServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var usage = await db.UsageRecords.SingleAsync(u => u.VirtualKeyId == key.Id, Cancellation);
            usage.McpTool.ShouldBe("get_image");
            usage.McpMethod.ShouldBe("tools/call");
            usage.Endpoint.ShouldBe(Domain.GatewayEndpoint.Mcp);
        }

        async Task<JsonNode> RpcAsync(string method, JsonObject parameters)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters };
            using var answer = await gateway.PostAsync("/mcp", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), Cancellation);
            return (await IntegrationFixture.ReadAsync(answer))["result"]!;
        }

        var resources = (await RpcAsync("resources/list", []))["resources"]!.AsArray();
        resources.Select(r => r!["uri"]!.GetValue<string>()).ShouldBe(["mcp+ume://demo/ume://demo/welcome"]);
        (await RpcAsync("resources/read", new JsonObject { ["uri"] = "mcp+ume://demo/ume://demo/welcome" }))["contents"]![0]!["text"]!
            .GetValue<string>().ShouldStartWith("Välkommen");
        (await RpcAsync("prompts/get", new JsonObject { ["name"] = "demo__summarize", ["arguments"] = new JsonObject { ["text"] = "hej" } }))["messages"]![0]!
            ["content"]!["text"]!.GetValue<string>().ShouldBe("Sammanfatta kort: hej");
    }

    [Fact]
    public async Task A_hosted_mcp_tool_on_responses_asks_for_approval_and_continues_through_redis()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        using (var scope = fixture.GatewayServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var demo = await db.McpServers.SingleAsync(s => s.Slug == "demo", Cancellation);
            db.VirtualKeyMcpGrants.Add(new VirtualKeyMcpGrant { VirtualKeyId = key.Id, McpServerId = demo.Id, AllowedTools = ["echo"], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Cancellation);
        }

        await fixture.GatewayServices.GetRequiredService<IInvalidationBus>().PublishAsync(InvalidationKind.Config, Cancellation);
        using var gateway = fixture.GatewayClient(key.Secret);
        var tool = new JsonObject { ["type"] = "mcp", ["server_label"] = "demo" };
        var first = new JsonObject
        {
            ["model"] = "ume/chat-onprem", ["input"] = """[[call:demo__echo {"text":"hej"}]]""", ["tools"] = new JsonArray(tool),
        };
        using var stopped = await gateway.PostAsync("/v1/responses", new StringContent(first.ToJsonString(), Encoding.UTF8, "application/json"), Cancellation);
        var approval = (await IntegrationFixture.ReadAsync(stopped))!.AsObject();
        var request = approval["output"]!.AsArray().Single(i => i!["type"]!.GetValue<string>() == "mcp_approval_request")!;
        request["name"]!.GetValue<string>().ShouldBe("echo");

        var next = new JsonObject
        {
            ["model"] = "ume/chat-onprem", ["previous_response_id"] = approval["id"]!.GetValue<string>(), ["tools"] = new JsonArray(tool.DeepClone()),
            ["input"] = new JsonArray(new JsonObject { ["type"] = "mcp_approval_response", ["approval_request_id"] = request["id"]!.GetValue<string>(), ["approve"] = true }),
        };
        using var continued = await gateway.PostAsync("/v1/responses", new StringContent(next.ToJsonString(), Encoding.UTF8, "application/json"), Cancellation);
        var done = (await IntegrationFixture.ReadAsync(continued))!.AsObject();
        var call = done["output"]!.AsArray().Single(i => i!["type"]!.GetValue<string>() == "mcp_call")!;
        call["output"]!.GetValue<string>().ShouldBe("hej");
        done["output"]!.AsArray().Last()!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Tool results: hej");

        using var again = await gateway.PostAsync("/v1/responses", new StringContent(next.ToJsonString(), Encoding.UTF8, "application/json"), Cancellation);
        ((int)again.StatusCode).ShouldBe(404);
    }

    [Fact]
    public async Task Redis_keeps_mcp_loop_state_per_key_and_hands_it_out_once()
    {
        var store = fixture.GatewayServices.GetRequiredService<IMcpLoopStore>();
        store.ShouldBeOfType<RedisMcpLoopStore>();
        var keyId = Guid.NewGuid();

        await store.SaveAsync(keyId, [(McpLoopRecord.Approval, "a", [1, 2, 3])], TimeSpan.FromMinutes(1), Cancellation);
        (await store.GetAsync(Guid.NewGuid(), McpLoopRecord.Approval, "a", Cancellation)).ShouldBeNull();
        (await store.GetAsync(keyId, McpLoopRecord.Pending, "a", Cancellation)).ShouldBeNull();
        (await store.GetAsync(keyId, McpLoopRecord.Approval, "a", Cancellation)).ShouldBe(new byte[] { 1, 2, 3 });
        (await store.TakeAsync(keyId, McpLoopRecord.Approval, "a", Cancellation)).ShouldBe(new byte[] { 1, 2, 3 });
        (await store.TakeAsync(keyId, McpLoopRecord.Approval, "a", Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task Redis_counts_mcp_calls_per_key_and_minute_across_requests()
    {
        var limiter = fixture.GatewayServices.GetRequiredService<IRateLimiter>();
        limiter.ShouldBeOfType<RedisRateLimiter>();
        var keyId = Guid.NewGuid();

        (await limiter.AcquireMcpCallAsync(keyId, 2, Cancellation)).Allowed.ShouldBeTrue();
        (await limiter.AcquireMcpCallAsync(keyId, 2, Cancellation)).Allowed.ShouldBeTrue();
        var refused = await limiter.AcquireMcpCallAsync(keyId, 2, Cancellation);
        refused.Allowed.ShouldBeFalse();
        refused.RetryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
        (await limiter.AcquireMcpCallAsync(keyId, null, Cancellation)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_caps_listen_streams_per_key_across_instances()
    {
        var registry = fixture.GatewayServices.GetRequiredService<IMcpListenRegistry>();
        registry.ShouldBeOfType<RedisMcpListenRegistry>();
        var keyId = Guid.NewGuid();

        (await registry.TryOpenAsync(keyId, "a", 1, TimeSpan.FromMinutes(1), Cancellation)).ShouldBeTrue();
        (await registry.TryOpenAsync(keyId, "b", 1, TimeSpan.FromMinutes(1), Cancellation)).ShouldBeFalse();
        await registry.CloseAsync(keyId, "a", Cancellation);
        (await registry.TryOpenAsync(keyId, "b", 1, TimeSpan.FromMinutes(1), Cancellation)).ShouldBeTrue();
    }

    [Fact]
    public async Task Syncing_the_seeded_demo_server_finds_its_tools_unchanged()
    {
        using var admin = await fixture.AdminAsync();
        using var list = await admin.GetAsync("/api/mcp/servers", Cancellation);
        var demo = (await IntegrationFixture.ReadAsync(list)).AsArray().Single(s => s!["slug"]!.GetValue<string>() == "demo")!;
        using var sync = await admin.PostAsync($"/api/mcp/servers/{demo["id"]}/sync", null, Cancellation);
        var result = await IntegrationFixture.ReadAsync(sync);
        result["status"]!.GetValue<string>().ShouldBe("Healthy");
        foreach (var changes in new[] { "added", "changed", "reverted", "updated", "removed", "errors" })
        {
            result[changes]!.AsArray().ShouldBeEmpty(changes);
        }
    }

    [Fact]
    public async Task A_tool_approved_in_the_admin_api_reaches_the_gateway()
    {
        using var admin = await fixture.AdminAsync();
        var key = await fixture.KeyAsync(admin);
        var slug = "it" + Guid.NewGuid().ToString("N")[..10];
        using var create = await admin.PostAsJsonAsync("/api/mcp/servers", new { slug, displayName = "Integration", url = fixture.FakeMcpUrl, residency = "OnPrem" }, Cancellation);
        var server = (await IntegrationFixture.ReadAsync(create))["id"]!.GetValue<Guid>();
        using (var sync = await admin.PostAsync($"/api/mcp/servers/{server}/sync", null, Cancellation))
        {
            (await IntegrationFixture.ReadAsync(sync))["added"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["echo", "get_image", "get_time"]);
        }

        using (var grant = await admin.PutAsJsonAsync($"/api/keys/{key.Id}/mcp-grants", new[] { new { serverId = server, allowedTools = new[] { "*" } } }, Cancellation))
        {
            grant.EnsureSuccessStatusCode();
        }

        using var gateway = fixture.GatewayClient(key.Secret);
        async Task<JsonNode?> RpcAsync(string method, JsonObject parameters)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters };
            using var answer = await gateway.PostAsync($"/mcp/{slug}", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), Cancellation);
            return answer.IsSuccessStatusCode ? JsonNode.Parse(await answer.Content.ReadAsStringAsync(Cancellation)) : null;
        }

        async Task<string[]> ToolsAsync() =>
            [.. (await RpcAsync("tools/list", []))!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];

        // The gateway reloads its catalogue when the admin API's invalidation arrives over Redis.
        await WaitForAsync(async () => await RpcAsync("ping", []) is not null);
        (await ToolsAsync()).ShouldBeEmpty();

        using var tools = await admin.GetAsync($"/api/mcp/servers/{server}/tools", Cancellation);
        var echo = (await IntegrationFixture.ReadAsync(tools)).AsArray().Single(t => t!["name"]!.GetValue<string>() == "echo")!;
        using (var approve = await admin.PostAsJsonAsync($"/api/mcp/servers/{server}/tools/{echo["id"]}/approve", new { definitionHash = echo["definitionHash"]!.GetValue<string>() }, Cancellation))
        {
            approve.EnsureSuccessStatusCode();
        }

        await WaitForAsync(async () => (await ToolsAsync()).SequenceEqual(["echo"]));
        var call = await RpcAsync("tools/call", new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["text"] = "hej" } });
        call!["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("hej");
    }

    [Fact]
    public async Task Redis_lets_one_holder_take_the_mcp_sync_lock()
    {
        var syncLock = fixture.GatewayServices.GetRequiredService<IMcpSyncLock>();
        syncLock.ShouldBeOfType<RedisMcpSyncLock>();
        var token = (await syncLock.TryAcquireAsync(TimeSpan.FromSeconds(30), Cancellation)).ShouldNotBeNull();
        try
        {
            (await syncLock.TryAcquireAsync(TimeSpan.FromSeconds(30), Cancellation)).ShouldBeNull();
            (await syncLock.RenewAsync(token, TimeSpan.FromSeconds(30), Cancellation)).ShouldBeTrue();
            (await syncLock.RenewAsync("someone-else", TimeSpan.FromSeconds(30), Cancellation)).ShouldBeFalse();
            await syncLock.ReleaseAsync("someone-else", Cancellation);
            (await syncLock.TryAcquireAsync(TimeSpan.FromSeconds(30), Cancellation)).ShouldBeNull();
        }
        finally
        {
            await syncLock.ReleaseAsync(token, Cancellation);
        }

        var next = (await syncLock.TryAcquireAsync(TimeSpan.FromSeconds(30), Cancellation)).ShouldNotBeNull();
        await syncLock.ReleaseAsync(next, Cancellation);
    }

    [Fact]
    public async Task The_gateways_mcp_client_never_connects_to_forbidden_addresses()
    {
        var handlers = fixture.GatewayServices.GetRequiredService<IHttpMessageHandlerFactory>();
        foreach (var name in new[] { McpUpstream.HttpClientName, McpTokenCache.HttpClientName })
        {
            using var http = new HttpClient(handlers.CreateHandler(name), disposeHandler: false);
            var refused = await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync("http://169.254.169.254/latest/meta-data/", Cancellation));
            refused.ToString().ShouldContain("not allowed", customMessage: name);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "The gateway did not see the change in time.");
            await Task.Delay(100, Cancellation);
        }
    }
}
