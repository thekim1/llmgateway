using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>
/// Migration <c>DeploymentResidencyAndAddress</c> (0.9.0 to the next release): four nullable model columns. Existing models
/// keep their provider's residency and address, and the previous version, which knows none of the columns, keeps working
/// against the new schema during a rolling update.
/// </summary>
public sealed class DeploymentResidencyMigrationTests(DeploymentResidencyFixture fixture) : IClassFixture<DeploymentResidencyFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Existing_models_keep_their_providers_residency_and_address()
    {
        await using var db = TestPostgres.Context(fixture.ConnectionString);
        var model = await db.ModelDeployments.AsNoTracking().Include(m => m.ProviderAccount).SingleAsync(m => m.Id == DeploymentResidencyFixture.ModelId, Ct);

        (model.Residency, model.Publisher, model.RequestBaseUrl, model.RequestAuthMode).ShouldBe((null, null, null, null));
        model.EffectiveResidency(model.ProviderAccount!).ShouldBe(DataResidency.Eu);
    }

    [Fact]
    public async Task The_previous_version_can_write_and_read_models_against_the_new_schema()
    {
        // What 0.9.0's EF Core sends: only the columns it knows.
        var id = Guid.NewGuid();
        await DeploymentResidencyFixture.ExecuteAsync(fixture.ConnectionString, $"""
            INSERT INTO "ModelDeployments" ("Id", "ProviderAccountId", "Name", "UpstreamModel", "BaseModel", "Kind", "ParameterProfile",
                    "ContextWindow", "Features", "IsEnabled", "AutoUpdatePrice", "Description")
                VALUES ('{id}', '{DeploymentResidencyFixture.ProviderId}', 'old-instance', 'old-instance', NULL, 'Rerank', 'Standard',
                    NULL, ARRAY[]::text[], true, true, NULL);
            UPDATE "ModelDeployments" SET "Description" = 'changed by 0.9.0' WHERE "Id" = '{id}';
            """);
        var names = await DeploymentResidencyFixture.QueryAsync(fixture.ConnectionString, """
            SELECT m."Name" FROM "ModelDeployments" AS m INNER JOIN "ProviderAccounts" AS p ON p."Id" = m."ProviderAccountId" ORDER BY m."Name"
            """);
        names.ShouldBe(["gpt-4.1", "old-instance"]);

        await using var db = TestPostgres.Context(fixture.ConnectionString);
        var written = await db.ModelDeployments.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
        (written.Residency, written.RequestBaseUrl, written.Description).ShouldBe((null, null, "changed by 0.9.0"));
    }

    [Fact]
    public async Task The_schema_matches_the_model_and_every_migration_is_applied()
    {
        await using var db = TestPostgres.Context(fixture.ConnectionString);

        db.Database.HasPendingModelChanges().ShouldBeFalse();
        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
        (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldContain(m => m.EndsWith("_DeploymentResidencyAndAddress", StringComparison.Ordinal));
    }
}

/// <summary>A database migrated to 0.9.0 (<c>KeyResidencyCeiling</c>) with a provider and a model, then to the current version.</summary>
public sealed class DeploymentResidencyFixture : IAsyncLifetime
{
    public static readonly Guid ProviderId = Guid.Parse("00000000-0000-0000-0000-0000000f0001");
    public static readonly Guid ModelId = Guid.Parse("00000000-0000-0000-0000-0000000f0002");
    private const string Release090 = "20261105232011_KeyResidencyCeiling";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var db = TestPostgres.Context(ConnectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(Release090);
        }

        await ExecuteAsync(ConnectionString, $"""
            INSERT INTO "ProviderAccounts" ("Id", "Name", "Type", "BaseUrl", "AuthMode", "Residency", "Capabilities", "IsEnabled", "IsDrained",
                    "TimeoutSeconds", "AutoUpdatePrices", "CreatedAt")
                VALUES ('{ProviderId}', 'foundry', 'AzureAIFoundry', 'https://res.services.ai.azure.com/api/projects/umea', 'ApiKeyHeader', 'Eu',
                    0, true, false, 120, true, now());
            INSERT INTO "ModelDeployments" ("Id", "ProviderAccountId", "Name", "UpstreamModel", "Kind", "ParameterProfile", "Features",
                    "IsEnabled", "AutoUpdatePrice")
                VALUES ('{ModelId}', '{ProviderId}', 'gpt-4.1', 'gpt-4.1', 'Chat', 'Standard', ARRAY[]::text[], true, true);
            """);
        await TestPostgres.MigrateAsync(ConnectionString);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
