extern alias Gateway;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Ume.LlmGateway.Domain.Entities;
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
}
