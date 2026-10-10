using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.MigrationService;

/// <summary>
/// Applies EF Core migrations (expand/contract, backwards compatible) and optional dev seed data, then exits.
/// Gateway and admin API wait for this to complete, so they never run against an older schema.
/// </summary>
public sealed partial class MigrationWorker(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    private static readonly ActivitySource Source = new(Extensions.TelemetryName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = Source.StartActivity("Migrate database", ActivityKind.Internal);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(() => db.Database.MigrateAsync(stoppingToken));
            if (logger.IsEnabled(LogLevel.Information))
            {
                LogMigrated(logger, (await db.Database.GetAppliedMigrationsAsync(stoppingToken)).LastOrDefault());
            }

            var rolesPath = configuration["Migration:RolesSqlPath"];
            if (!string.IsNullOrWhiteSpace(rolesPath))
            {
                await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(rolesPath, stoppingToken), stoppingToken);
            }

            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(db, stoppingToken);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            LogFailed(logger, ex);
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(LogLevel.Information, "Database schema is at migration {Migration}")]
    private static partial void LogMigrated(ILogger logger, string? migration);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
