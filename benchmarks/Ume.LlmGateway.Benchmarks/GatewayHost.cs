using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Benchmarks;

public enum StoreKind
{
    InMemory,
    Redis,
}

/// <summary>
/// The real gateway (same Program, DI and pipeline) on Postgres and optionally Redis in containers, with the
/// provider replaced by <see cref="StubUpstream"/>. The key has rate limits and key/team/department budgets so
/// every per-request step runs; the limits are high enough never to reject.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    public const string ChatAlias = "ume/chat";
    public const string EmbeddingsAlias = "ume/embed";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer? _redis;
    private WebApplicationFactory<Program> _factory = null!;

    private readonly IReadOnlyDictionary<string, string> _settings;

    private GatewayHost(StoreKind store, IReadOnlyDictionary<string, string>? settings)
    {
        _settings = settings ?? new Dictionary<string, string>();
        _redis = store == StoreKind.Redis ? new RedisBuilder("redis:7.4-alpine").Build() : null;
    }

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public string Key { get; private set; } = null!;

    public static async Task<GatewayHost> StartAsync(StoreKind store, IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = new GatewayHost(store, settings);
        await host.InitializeAsync();
        return host;
    }

    private async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis?.StartAsync() ?? Task.CompletedTask);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(GatewayContentRoot());
            builder.UseSetting("ConnectionStrings:gatewaydb", _postgres.GetConnectionString());
            if (_redis is not null)
            {
                builder.UseSetting("ConnectionStrings:redis", _redis.GetConnectionString());
            }

            builder.UseSetting("Security:KeyPepper", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            foreach (var (name, value) in _settings)
            {
                builder.UseSetting(name, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ProviderHttpClient>();
                services.AddSingleton(new ProviderHttpClient(new StubUpstream()));
            });
        });

        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.Database.MigrateAsync();
            var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
            Key = await SeedAsync(db, scope.ServiceProvider.GetRequiredService<VirtualKeyHasher>(), name => $"http://{name}.upstream/v1", "bench", protector.Protect("upstream-secret"));
        }

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    /// <summary>
    /// Seeds a department, team, key (rate limits plus key/team/department budgets, never reached), two providers with
    /// a chat and an embeddings deployment each, and the <see cref="ChatAlias"/>/<see cref="EmbeddingsAlias"/> routes
    /// with the second provider as fallback. Returns the key's plaintext.
    /// </summary>
    public static async Task<string> SeedAsync(GatewayDbContext db, VirtualKeyHasher hasher, Func<string, string> providerBaseUrl, string upstreamModelPrefix, string? encryptedCredential)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(providerBaseUrl);
        var now = DateTimeOffset.UtcNow;
        var department = new Department { Name = "Bench", CostCenterCode = "BENCH", CreatedAt = now };
        var team = new Team { Name = "Bench", DepartmentId = department.Id, CreatedAt = now };
        var generated = hasher.Generate();
        var key = new VirtualKey
        {
            Name = "bench", TeamId = team.Id, Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = now,
            RequestsPerMinute = 100_000_000, TokensPerMinute = 2_000_000_000,
        };
        db.AddRange(department, team, key);
        db.ExchangeRates.Add(new ExchangeRate { SekPerUnit = 10, EffectiveFrom = now.AddDays(-1) });
        foreach (var (scope, id) in new[] { (BudgetScope.VirtualKey, key.Id), (BudgetScope.Team, team.Id), (BudgetScope.Department, department.Id) })
        {
            db.Budgets.Add(new Budget { Scope = scope, ScopeId = id, LimitSek = 1_000_000_000, Period = BudgetPeriod.Monthly, CreatedAt = now });
        }

        var deployments = new List<ModelDeployment>();
        foreach (var name in new[] { "primary", "fallback" })
        {
            var provider = new ProviderAccount
            {
                Name = name, Type = ProviderType.OpenAICompatible, Residency = DataResidency.Eu, BaseUrl = providerBaseUrl(name),
                AuthMode = ProviderAuthMode.Bearer, EncryptedCredential = encryptedCredential, TimeoutSeconds = 60, CreatedAt = now,
                Capabilities = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming | ProviderCapabilities.Embeddings,
            };
            foreach (var (model, kind) in new[] { ("chat", ModelKind.Chat), ("embed", ModelKind.Embedding) })
            {
                var deployment = new ModelDeployment
                {
                    Name = $"{name}/{model}", UpstreamModel = $"{upstreamModelPrefix}-{model}", Kind = kind,
                    Prices = [new ModelPrice { InputPerMillionUsd = 2.5m, CachedInputPerMillionUsd = 1.25m, OutputPerMillionUsd = 10, EffectiveFrom = now.AddDays(-30) }],
                };
                provider.Deployments.Add(deployment);
                deployments.Add(deployment);
            }

            db.ProviderAccounts.Add(provider);
        }

        db.RouteAliases.Add(new RouteAlias
        {
            Name = ChatAlias, Kind = ModelKind.Chat,
            Targets = [.. deployments.Where(d => d.Kind == ModelKind.Chat).Select((d, i) => new RouteTarget { ModelDeploymentId = d.Id, Priority = i })],
        });
        db.RouteAliases.Add(new RouteAlias
        {
            Name = EmbeddingsAlias, Kind = ModelKind.Embedding,
            Targets = [.. deployments.Where(d => d.Kind == ModelKind.Embedding).Select((d, i) => new RouteTarget { ModelDeploymentId = d.Id, Priority = i })],
        });
        await db.SaveChangesAsync();
        return generated.PlainText;
    }

    /// <summary>The gateway's source folder (appsettings.json), found from the binary so BenchmarkDotNet's child processes work too.</summary>
    private static string GatewayContentRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Ume.LlmGateway.Gateway");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not find src/Ume.LlmGateway.Gateway above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Sends a request and reads the whole response, failing loudly on any status other than the expected one (success
    /// by default) so a benchmark never measures an error path by accident.
    /// </summary>
    public async Task<int> SendAsync(string path, byte[] body, string? key = null, int? expectStatus = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key ?? Key);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (expectStatus is { } expected ? (int)response.StatusCode != expected : !response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{path} returned {(int)response.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");
        }

        return bytes.Length;
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }
}
