extern alias Gateway;
extern alias Fake;
extern alias Migration;

using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

[assembly: AssemblyFixture(typeof(Ume.LlmGateway.IntegrationTests.IntegrationFixture))]

namespace Ume.LlmGateway.IntegrationTests;

public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly string RedisPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine")
        .WithEnvironment("REDIS_PASSWORD", RedisPassword)
        .WithCommand("sh", "-c", "printf 'requirepass %s\\n' \"$REDIS_PASSWORD\" | exec redis-server -")
        .Build();
    private readonly string _pepper = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplicationFactory<Program> _admin = null!;
    private WebApplicationFactory<Gateway::Program> _gateway = null!;
    private WebApplicationFactory<Fake::Program> _fake = null!;
    public IServiceProvider GatewayServices => _gateway.Services;
    public string RedisHost => _redis.Hostname;
    public ushort RedisPort => _redis.GetMappedPublicPort(6379);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _fake = new WebApplicationFactory<Fake::Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        _fake.UseKestrel();
        using var fakeClient = _fake.CreateClient();
        _gateway = new WebApplicationFactory<Gateway::Program>().WithWebHostBuilder(Configure);
        using (var scope = GatewayServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.Database.MigrateAsync();
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
            b.ConfigureTestServices(services => services.AddAuthentication(o =>
            {
                o.DefaultScheme = "Integration";
                o.DefaultAuthenticateScheme = "Integration";
                o.DefaultChallengeScheme = "Integration";
            }).AddScheme<AuthenticationSchemeOptions, IntegrationAuthentication>("Integration", _ => { }));
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
        var client = _admin.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var user = await client.GetAsync("/bff/user");
        user.EnsureSuccessStatusCode();
        var token = user.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).Split(';')[0]["XSRF-TOKEN=".Length..];
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    public HttpClient GatewayClient(string secret)
    {
        var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return client;
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

public sealed class IntegrationAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "integration-user"), new Claim("name", "Integration"), new Claim("roles", "gateway-admin")],
            Scheme.Name, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
