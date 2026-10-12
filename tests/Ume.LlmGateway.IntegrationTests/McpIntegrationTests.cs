extern alias Gateway;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Ume.LlmGateway.Infrastructure.Persistence;
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
}
