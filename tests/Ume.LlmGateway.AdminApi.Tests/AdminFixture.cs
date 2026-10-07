using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

[assembly: AssemblyFixture(typeof(Ume.LlmGateway.AdminApi.Tests.AdminFixture))]

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class AdminFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;
    public IServiceProvider Services => _factory.Services;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gatewaydb", _postgres.GetConnectionString());
            builder.UseSetting("Security:KeyPepper", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Oidc:Authority", "https://identity.invalid");
            builder.UseSetting("Admin:RequestsPerMinute", "10000");
            builder.ConfigureTestServices(services =>
            {
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    o.Configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = "https://identity.invalid", AuthorizationEndpoint = "https://identity.invalid/authorize",
                        TokenEndpoint = "https://identity.invalid/token", EndSessionEndpoint = "https://identity.invalid/logout",
                    });
                services.AddAuthentication(o =>
                {
                    o.DefaultScheme = "Test"; o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test"; o.DefaultForbidScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.MigrateAsync();
    }

    public async Task<HttpClient> ClientAsync(string? role = "gateway-admin", string? code = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Role", role);
            if (code is not null) { client.DefaultRequestHeaders.Add("X-Test-Department", code); }
        }
        using var user = await client.GetAsync("/bff/user");
        user.EnsureSuccessStatusCode();
        var token = user.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).Split(';')[0]["XSRF-TOKEN=".Length..];
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
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
        await _postgres.DisposeAsync();
    }
}

public sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (role.Length == 0) { return Task.FromResult(AuthenticateResult.NoResult()); }
        var claims = new List<Claim> { new("sub", "test-user"), new("name", "Test user"), new("roles", role) };
        foreach (var code in Request.Headers["X-Test-Department"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            claims.Add(new Claim("departmentCodes", code));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, "name", "roles"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
