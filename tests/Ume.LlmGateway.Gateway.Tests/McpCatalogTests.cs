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
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

internal static class McpTestData
{
    public const string Definition = """{"description":"d","inputSchema":{"type":"object"}}""";

    public static McpServer Server(string slug, params (string Name, McpToolApprovalState State)[] tools) => new()
    {
        Slug = slug,
        DisplayName = slug,
        Url = $"https://{slug}.test/mcp",
        Status = McpServerStatus.Healthy,
        CreatedAt = DateTimeOffset.UtcNow,
        Tools = [.. tools.Select(t => Tool(slug, t.Name, t.State))],
    };

    public static McpTool Tool(string slug, string name, McpToolApprovalState state) => new()
    {
        Name = name,
        Definition = Definition,
        DefinitionHash = new string('0', 64),
        ApprovalState = state,
        ApprovedDefinition = state is McpToolApprovalState.Pending ? null : Definition,
        ExposedName = McpToolNames.Exposed(slug, name),
        DiscoveredAt = DateTimeOffset.UtcNow,
    };

    public static VirtualKeyMcpGrant Grant(Guid keyId, McpServer server, params string[] tools) =>
        new() { VirtualKeyId = keyId, McpServerId = server.Id, AllowedTools = [.. tools], CreatedAt = DateTimeOffset.UtcNow };
}

public sealed class McpCatalogSnapshotTests
{
    private static CatalogSnapshot Snapshot(IReadOnlyList<McpServer> servers, IReadOnlyList<VirtualKeyMcpGrant>? grants = null) =>
        new([], [], [], 10m, DateTimeOffset.UtcNow, mcpServers: servers, mcpGrants: grants);

    [Fact]
    public void Only_approved_and_changed_tools_are_indexed_by_exposed_and_upstream_name()
    {
        var diarium = McpTestData.Server("diarium",
            ("search.cases", McpToolApprovalState.Approved), ("get_case", McpToolApprovalState.Changed),
            ("new_tool", McpToolApprovalState.Pending), ("bad_tool", McpToolApprovalState.Rejected));
        var snapshot = Snapshot([diarium]);

        snapshot.McpServers["diarium"].ShouldBeSameAs(diarium);
        snapshot.McpServerById(diarium.Id).ShouldBeSameAs(diarium);
        snapshot.McpTools.Keys.Order(StringComparer.Ordinal).ShouldBe(["diarium__get_case", "diarium__search_cases"]);
        snapshot.McpTools["diarium__search_cases"].Server.ShouldBeSameAs(diarium);
        snapshot.McpTools["diarium__search_cases"].Tool.Name.ShouldBe("search.cases");
        snapshot.McpServerTools(diarium.Id).Keys.Order(StringComparer.Ordinal).ShouldBe(["get_case", "search.cases"]);
        snapshot.McpServerTools(Guid.NewGuid()).ShouldBeEmpty();
        snapshot.McpDuplicateNames.ShouldBeEmpty();
    }

    [Fact]
    public void Tool_names_are_case_sensitive()
    {
        var server = McpTestData.Server("s", ("Echo", McpToolApprovalState.Approved), ("echo", McpToolApprovalState.Approved));
        var snapshot = Snapshot([server]);

        snapshot.McpTools.Keys.Order(StringComparer.Ordinal).ShouldBe(["s__Echo", "s__echo"]);
        snapshot.McpTools.ContainsKey("S__ECHO").ShouldBeFalse();
    }

    [Fact]
    public void An_exposed_name_collision_keeps_the_first_tool_and_is_reported()
    {
        var first = McpTestData.Server("a", ("x.y", McpToolApprovalState.Approved));
        var second = McpTestData.Server("b", ("z", McpToolApprovalState.Approved));
        var colliding = McpTestData.Tool("b", "x_y", McpToolApprovalState.Approved);
        colliding.ExposedName = "a__x_y";
        second.Tools.Add(colliding);
        var duplicateSlug = McpTestData.Server("a", ("other", McpToolApprovalState.Approved));
        var snapshot = Snapshot([first, second, duplicateSlug]);

        snapshot.McpTools["a__x_y"].Server.ShouldBeSameAs(first);
        snapshot.McpServerTools(second.Id).Keys.ShouldBe(["z"]);
        snapshot.McpServers["a"].ShouldBeSameAs(first);
        snapshot.McpTools.ContainsKey("a__other").ShouldBeFalse();
        snapshot.McpDuplicateNames.Order(StringComparer.Ordinal).ShouldBe(["a", "a__x_y"]);
    }

    [Fact]
    public void Grants_are_indexed_per_key_and_server_and_a_key_without_grants_has_none()
    {
        var one = McpTestData.Server("one", ("t", McpToolApprovalState.Approved));
        var two = McpTestData.Server("two", ("t", McpToolApprovalState.Approved));
        var key = Guid.NewGuid();
        var other = Guid.NewGuid();
        var orphan = new VirtualKeyMcpGrant { VirtualKeyId = key, McpServerId = Guid.NewGuid(), AllowedTools = ["*"] };
        var snapshot = Snapshot([one, two],
            [McpTestData.Grant(key, one, "*"), McpTestData.Grant(key, two, "t"), McpTestData.Grant(other, two), orphan]);

        snapshot.McpGrants(key).Keys.ShouldBe([one.Id, two.Id], ignoreOrder: true);
        snapshot.McpGrants(key)[two.Id].Allows("t").ShouldBeTrue();
        snapshot.McpGrants(other)[two.Id].Allows("t").ShouldBeFalse();
        snapshot.McpGrants(Guid.NewGuid()).ShouldBeEmpty();
    }

    [Fact]
    public void A_catalogue_without_mcp_configuration_is_empty()
    {
        var snapshot = new CatalogSnapshot([], [], [], 10m, DateTimeOffset.UtcNow);

        snapshot.McpServers.ShouldBeEmpty();
        snapshot.McpTools.ShouldBeEmpty();
        snapshot.McpGrants(Guid.NewGuid()).ShouldBeEmpty();
    }
}

/// <summary>MCP configuration reaches the catalogue from PostgreSQL and follows configuration invalidation.</summary>
public sealed class McpCatalogTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mcp_servers_tools_and_grants_load_after_a_config_invalidation()
    {
        var key = await fixture.CreateKeyAsync();
        var slug = "t" + Guid.NewGuid().ToString("N")[..12];
        var server = McpTestData.Server(slug, ("echo", McpToolApprovalState.Approved), ("pending", McpToolApprovalState.Pending));
        var bus = new InMemoryInvalidationBus();
        using var catalog = new GatewayCatalog(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions(new GatewayOptions { CatalogCacheSeconds = 300 }), new FakeTimeProvider(DateTimeOffset.UtcNow), bus,
            NullLogger<GatewayCatalog>.Instance);
        var before = await catalog.GetAsync(Ct);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.McpServers.Add(server);
            db.VirtualKeyMcpGrants.Add(McpTestData.Grant(key.Id, server, "echo"));
            await db.SaveChangesAsync(Ct);
        }

        try
        {
            (await catalog.GetAsync(Ct)).ShouldBeSameAs(before);
            before.McpServers.ContainsKey(slug).ShouldBeFalse();

            await bus.PublishAsync(InvalidationKind.Config, Ct);
            var after = await catalog.GetAsync(Ct);

            after.McpServers[slug].Id.ShouldBe(server.Id);
            after.McpServerTools(server.Id).Keys.ShouldBe(["echo"]);
            after.McpTools[$"{slug}__echo"].Tool.ApprovedDefinition.ShouldNotBeNull();
            after.McpTools.ContainsKey($"{slug}__pending").ShouldBeFalse();
            after.McpGrants(key.Id)[server.Id].AllowedTools.ShouldBe(["echo"]);
        }
        finally
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.McpServers.Where(s => s.Id == server.Id).ExecuteDeleteAsync(Ct);
        }
    }

    private sealed class StaticOptions(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
