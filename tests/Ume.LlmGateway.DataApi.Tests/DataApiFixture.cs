using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.TestKit;

[assembly: AssemblyFixture(typeof(Ume.LlmGateway.DataApi.Tests.DataApiFixture))]
// Feeds read the whole table from a cursor; one test at a time keeps each test's rows contiguous.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Ume.LlmGateway.DataApi.Tests;

/// <summary>Real Postgres and real JWT validation; tokens are signed with a key the API is configured to trust.</summary>
public sealed class DataApiFixture : IAsyncLifetime
{
    public const string Issuer = "https://identity.test/realms/ume";
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().WithDatabase("gatewaydb").Build();
    private readonly SymmetricSecurityKey _signingKey = new(RandomNumberGenerator.GetBytes(32));
    private WebApplicationFactory<Program> _factory = null!;
    public IServiceProvider Services => _factory.Services;
    public string DataConnectionString { get; private set; } = string.Empty;
    public CapturingLoggerProvider Logs { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var owner = OwnerContext())
        {
            await owner.Database.MigrateAsync();
            // The API runs as the production read-only role with the real grants, so a query that touches a column
            // the role may not read (a key hash, a provider credential) fails these tests.
            await owner.Database.ExecuteSqlRawAsync("CREATE ROLE ume_gateway NOLOGIN; CREATE ROLE ume_admin NOLOGIN; CREATE ROLE ume_data LOGIN PASSWORD 'data-test';");
            await owner.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(RepoFile("deploy", "postgres", "app-roles.sql")));
        }

        var dataConnection = DataConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Username = "ume_data", Password = "data-test" }.ConnectionString;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gatewaydb", dataConnection);
            builder.UseSetting("DataApi:Authority", Issuer);
            builder.UseSetting("DataApi:SettleSeconds", "0");
            builder.UseSetting("DataApi:MinimumGroupSize", "2");
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // No metadata download: the issuer and key are given directly.
                o.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                o.Configuration.SigningKeys.Add(_signingKey);
                o.TokenValidationParameters.ValidIssuer = Issuer;
                o.TokenValidationParameters.IssuerSigningKey = _signingKey;
            }));
        });
    }

    /// <summary>A context with the database owner's rights, for migrations and test data.</summary>
    public GatewayDbContext OwnerContext() => TestPostgres.Context(_postgres.GetConnectionString());

    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }

    /// <summary>A client whose permissions are in <paramref name="claim"/>: "scope" (space-separated) or "roles" (Entra ID style).</summary>
    public HttpClient Client(string permissions, string claim = "scope", string audience = "ume-data-api", string client = "etl-test")
    {
        var http = _factory.CreateHttpsClient();
        var claims = new Dictionary<string, object> { ["azp"] = client };
        claims[claim] = claim == "scope" ? permissions : permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = audience, Expires = DateTime.UtcNow.AddMinutes(5), Claims = claims,
            Subject = new ClaimsIdentity([new Claim("sub", "service-account-" + client)]),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public HttpClient Anonymous() => _factory.CreateHttpsClient(allowAutoRedirect: true);

    public async Task<T> DbAsync<T>(Func<GatewayDbContext, Task<T>> work)
    {
        await using var db = OwnerContext();
        return await work(db);
    }

    /// <summary>The highest usage record id so far: a test reads its own rows from here.</summary>
    public Task<long> UsageCursorAsync() => DbAsync(async db => await db.UsageRecords.MaxAsync(u => (long?)u.Id) ?? 0);

    public static UsageRecord Usage(Guid department, Guid? key = null, DateTimeOffset? at = null, Action<UsageRecord>? configure = null)
    {
        var record = new UsageRecord
        {
            RequestId = Guid.NewGuid().ToString("N"), Timestamp = at ?? DateTimeOffset.UtcNow, VirtualKeyId = key ?? Guid.NewGuid(),
            TeamId = Guid.NewGuid(), DepartmentId = department, Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = "ume/chat",
            InputTokens = 100, OutputTokens = 20, CostSek = 1.5m, CostUsd = 0.15m, StatusCode = 200, Outcome = RequestOutcome.Success,
        };
        configure?.Invoke(record);
        return record;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
