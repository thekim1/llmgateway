extern alias Gateway;
extern alias Fake;
extern alias Migration;

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.TestKit;

[assembly: AssemblyFixture(typeof(Ume.LlmGateway.IntegrationTests.IntegrationFixture))]

namespace Ume.LlmGateway.IntegrationTests;

public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();
    private static readonly string RedisPassword = TestSecrets.RandomHex();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine")
        .WithEnvironment("REDIS_PASSWORD", RedisPassword)
        .WithCommand("sh", "-c", "printf 'requirepass %s\\n' \"$REDIS_PASSWORD\" | exec redis-server -")
        .Build();
    private readonly string _pepper = TestSecrets.RandomHex();
    private WebApplicationFactory<Program> _admin = null!;
    private WebApplicationFactory<Gateway::Program> _gateway = null!;
    private WebApplicationFactory<Fake::Program> _fake = null!;
    public IServiceProvider GatewayServices => _gateway.Services;
    public string RedisHost => _redis.Hostname;
    public ushort RedisPort => _redis.GetMappedPublicPort(6379);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        await TestPostgres.MigrateAsync(_postgres.GetConnectionString());
        _fake = new WebApplicationFactory<Fake::Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        _fake.UseKestrel();
        using var fakeClient = _fake.CreateClient();
        _gateway = new WebApplicationFactory<Gateway::Program>().WithWebHostBuilder(Configure);
        using (var scope = GatewayServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var seeder = new Migration::Ume.LlmGateway.MigrationService.DevSeeder(
                Options.Create(new Migration::Ume.LlmGateway.MigrationService.SeedOptions
                {
                    Enabled = true, FakeLlmUrl = fakeClient.BaseAddress!.ToString().TrimEnd('/') + "/v1",
                }),
                scope.ServiceProvider.GetRequiredService<CredentialProtector>(),
                scope.ServiceProvider.GetRequiredService<VirtualKeyHasher>(),
                TimeProvider.System,
                NullLogger<Migration::Ume.LlmGateway.MigrationService.DevSeeder>.Instance);
            await seeder.SeedAsync(db, CancellationToken.None);
        }
        _admin = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            Configure(b);
            b.UseSetting("Oidc:Authority", "https://identity.invalid");
            b.UseSetting("Admin:RequestsPerMinute", "10000");
            // Every admin request is the same administrator; no headers needed.
            b.ConfigureTestServices(services => services.AddHeaderAuthentication(o =>
            {
                o.DefaultRole = "gateway-admin";
                o.Subject = "integration-user";
                o.Name = "Integration";
            }));
        });
    }

    private void Configure(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:gatewaydb", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:redis", _redis.GetConnectionString() + ",password=" + RedisPassword);
        builder.UseSetting("Security:KeyPepper", _pepper);
        builder.UseSetting("Gateway:KeyCacheSeconds", "60");
        builder.UseSetting("Gateway:CatalogCacheSeconds", "60");
        builder.UseSetting("Gateway:DefaultOutputTokenEstimate", "32");
    }

    public async Task<HttpClient> AdminAsync()
    {
        return await _admin.CreateHttpsClient().WithXsrfTokenAsync();
    }

    public HttpClient GatewayClient(string secret)
    {
        var client = _gateway.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return client;
    }

    public Task<System.Net.WebSockets.WebSocket> GatewayRealtimeAsync(string secret, string model, string path = "/v1/realtime")
    {
        var client = _gateway.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + secret;
        return client.ConnectAsync(new Uri($"ws://localhost{path}?model={Uri.EscapeDataString(model)}"), TestContext.Current.CancellationToken);
    }

    public async Task<(Guid Id, string Secret)> KeyAsync(HttpClient admin, int? requestsPerMinute = null)
    {
        using var departments = await admin.GetAsync("/api/departments");
        var department = (await ReadAsync(departments)).AsArray()[0]!["id"]!.GetValue<Guid>();
        using var teamResponse = await admin.PostAsJsonAsync("/api/teams", new { departmentId = department, name = Guid.NewGuid().ToString("N") });
        var team = (await ReadAsync(teamResponse))["id"]!.GetValue<Guid>();
        using var keyResponse = await admin.PostAsJsonAsync("/api/keys", new
        {
            teamId = team, name = "Integration", allowedModels = Array.Empty<string>(),
            allowedResidencies = Array.Empty<string>(), piiPolicy = "RerouteToOnPrem", requestsPerMinute,
        });
        var result = await ReadAsync(keyResponse);
        return (result["key"]!["id"]!.GetValue<Guid>(), result["secret"]!.GetValue<string>());
    }

    public static async Task<JsonNode> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    public Task FlushAsync() =>
        GatewayServices.GetRequiredService<Gateway::Ume.LlmGateway.Gateway.Pipeline.UsageWriter>()
            .FlushAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _admin.DisposeAsync();
        await _gateway.DisposeAsync();
        await _fake.DisposeAsync();
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
