using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Infrastructure;

public static class InfrastructureExtensions
{
    public const string DatabaseResourceName = "gatewaydb";
    public const string RedisResourceName = "redis";
    public const string DataProtectionApplicationName = "Ume.LlmGateway";

    public static IHostApplicationBuilder AddDeploymentSecrets(this IHostApplicationBuilder builder)
    {
        var directory = builder.Configuration["Security:SecretsDirectory"];
        if (!string.IsNullOrWhiteSpace(directory))
        {
            builder.Configuration.AddKeyPerFile(directory, optional: false, reloadOnChange: false);
        }
        return builder;
    }

    /// <summary>EF Core (Npgsql) via the Aspire integration: pooling, retries, health check and tracing.</summary>
    public static IHostApplicationBuilder AddGatewayDatabase(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Environment.IsProduction())
        {
            var connection = new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString(DatabaseResourceName));
            if (connection.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(connection.RootCertificate) ||
                connection.Username is null or "postgres")
            {
                throw new InvalidOperationException("Production gatewaydb requires a non-superuser role and SSL Mode=VerifyFull with a Root Certificate.");
            }
        }
        builder.AddNpgsqlDbContext<GatewayDbContext>(DatabaseResourceName);
        return builder;
    }

    /// <summary>Key hashing pepper + Data Protection (provider credential encryption) shared by gateway and admin API.</summary>
    public static IHostApplicationBuilder AddGatewaySecurity(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<GatewaySecurityOptions>()
            .Bind(builder.Configuration.GetSection(GatewaySecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.TryAddSingleton(sp => KeyHasherFactory.Create(sp.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value));

        var dp = builder.Services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<GatewayDbContext>();

        // On-prem: protect the key ring at rest with a certificate (e.g. mounted secret). Without it the key ring is
        // stored unencrypted in Postgres (acceptable for local dev only – see docs/security-and-compliance.md).
        var certPath = builder.Configuration["DataProtection:CertificatePath"];
        if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(certPath))
        {
            throw new InvalidOperationException("Production requires DataProtection:CertificatePath to protect the shared key ring.");
        }
        if (!string.IsNullOrWhiteSpace(certPath))
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, builder.Configuration["DataProtection:CertificatePassword"]);
            dp.ProtectKeysWithCertificate(cert);
        }

        builder.Services.TryAddSingleton<CredentialProtector>();
        return builder;
    }

    /// <summary>
    /// Shared counters/state. Uses Redis when a <c>redis</c> connection string is configured, otherwise
    /// single-instance in-memory stores (local tests only – not suitable for multiple gateway replicas).
    /// </summary>
    public static IHostApplicationBuilder AddGatewayStores(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddOptions<CircuitBreakerOptions>().Bind(builder.Configuration.GetSection("CircuitBreaker"));

        if (builder.Environment.IsProduction())
        {
            var connection = builder.Configuration.GetConnectionString(RedisResourceName);
            if (string.IsNullOrWhiteSpace(connection) || !ConfigurationOptions.Parse(connection).Ssl ||
                ConfigurationOptions.Parse(connection).User is not "gateway")
            {
                throw new InvalidOperationException("Production Redis requires TLS and the restricted gateway ACL user.");
            }
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(RedisResourceName)))
        {
            builder.AddRedisClient(RedisResourceName);
            builder.Services.TryAddSingleton<IRateLimiter, RedisRateLimiter>();
            builder.Services.TryAddSingleton<ISpendLedger, RedisSpendLedger>();
            builder.Services.TryAddSingleton<IInvalidationBus, RedisInvalidationBus>();
            builder.Services.TryAddSingleton<ICircuitBreakerStore, RedisCircuitBreakerStore>();
            builder.Services.TryAddSingleton<IRealtimeSessionRegistry, RedisRealtimeSessionRegistry>();
        }
        else
        {
            builder.Services.TryAddSingleton<IRateLimiter, InMemoryRateLimiter>();
            builder.Services.TryAddSingleton<ISpendLedger, InMemorySpendLedger>();
            builder.Services.TryAddSingleton<IInvalidationBus, InMemoryInvalidationBus>();
            builder.Services.TryAddSingleton<ICircuitBreakerStore, InMemoryCircuitBreakerStore>();
            builder.Services.TryAddSingleton<IRealtimeSessionRegistry, InMemoryRealtimeSessionRegistry>();
        }

        return builder;
    }

    public static IServiceCollection AddProviderAdapters(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ProviderHttpClient(sp.GetRequiredService<IHostEnvironment>().IsProduction()));
        services.AddSingleton<IProviderAdapter, AnthropicAdapter>();
        services.AddSingleton<IProviderAdapter, OpenAICompatibleAdapter>();
        services.TryAddSingleton<IRealtimeConnector, RealtimeConnector>();
        return services;
    }

    /// <summary>Helper exposed for reuse by the seeding worker.</summary>
    public static VirtualKeyHasher CreateKeyHasher(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new GatewaySecurityOptions();
        configuration.GetSection(GatewaySecurityOptions.SectionName).Bind(options);
        return KeyHasherFactory.Create(options);
    }
}
