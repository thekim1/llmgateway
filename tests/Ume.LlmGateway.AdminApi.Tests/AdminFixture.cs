using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.TestKit;

[assembly: AssemblyFixture(typeof(Ume.LlmGateway.AdminApi.Tests.AdminFixture))]
// All tests share one database, and some act on all of it (configuration export and import, global routing rules,
// the exchange rate). Running test classes in parallel made those tests overwrite or collide with each other.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class AdminFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();
    private WebApplicationFactory<Program> _factory = null!;
    public IServiceProvider Services => _factory.Services;
    public CapturingLoggerProvider Logs { get; } = new();
    public McpTestServer Mcp { get; } = new();
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), Mcp.StartAsync());
        await TestPostgres.MigrateAsync(_postgres.GetConnectionString());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gatewaydb", _postgres.GetConnectionString());
            builder.UseSetting("Security:KeyPepper", TestSecrets.RandomHex());
            builder.UseSetting("Oidc:Authority", "https://identity.invalid");
            builder.UseSetting("Admin:RequestsPerMinute", "10000");
            // Tests sync MCP servers themselves; a scheduled sync would change their tools in between.
            builder.UseSetting("Admin:McpSyncMinutes", "0");
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMcpHostResolver, TestHostResolver>();
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    o.Configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = "https://identity.invalid", AuthorizationEndpoint = "https://identity.invalid/authorize",
                        TokenEndpoint = "https://identity.invalid/token", EndSessionEndpoint = "https://identity.invalid/logout",
                    });
                services.AddHeaderAuthentication();
            });
        });
    }

    public async Task<HttpClient> ClientAsync(string? role = "gateway-admin", string? code = null)
    {
        var client = _factory.CreateHttpsClient();
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, role);
            if (code is not null) { client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.DepartmentHeader, code); }
        }
        return await client.WithXsrfTokenAsync();
    }

    public async Task<(Guid Department, Guid Team, string Code)> OrganisationAsync(HttpClient client)
    {
        var code = Guid.NewGuid().ToString("N");
        using var dept = await client.PostAsJsonAsync("/api/departments", new { name = "Department " + code, costCenterCode = code });
        var d = await ReadAsync(dept, HttpStatusCode.Created);
        using var team = await client.PostAsJsonAsync("/api/teams", new { departmentId = d["id"]!.GetValue<Guid>(), name = "Team" });
        var t = await ReadAsync(team, HttpStatusCode.Created);
        return (d["id"]!.GetValue<Guid>(), t["id"]!.GetValue<Guid>(), code);
    }

    public static async Task<JsonNode> ReadAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, text);
        return JsonNode.Parse(text)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Mcp.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>
/// Host names for the SSRF tests without DNS: <c>*.public.test</c> is a public address, <c>*.private.test</c> a private
/// one, <c>*.metadata.test</c> the cloud metadata address and <c>*.mixed.test</c> both a public and a link-local one.
/// Other names are resolved as usual.
/// </summary>
internal sealed class TestHostResolver : IMcpHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => host switch
    {
        _ when host.EndsWith(".public.test", StringComparison.Ordinal) => Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.7")]),
        _ when host.EndsWith(".private.test", StringComparison.Ordinal) => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.1.2.3")]),
        _ when host.EndsWith(".metadata.test", StringComparison.Ordinal) => Task.FromResult<IPAddress[]>([IPAddress.Parse("169.254.169.254")]),
        _ when host.EndsWith(".mixed.test", StringComparison.Ordinal) => Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.8"), IPAddress.Parse("fe80::1")]),
        _ => Dns.GetHostAddressesAsync(host, ct),
    };
}
