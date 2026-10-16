using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>
/// Migration <c>BudgetsInUsd</c>: budget limits and stored alerts were in SEK and become USD, converted so a budget allows
/// what it allowed before the upgrade (the rate in effect now) and an alert keeps its amounts (the rate when it was raised).
/// </summary>
public sealed class BudgetsInUsdMigrationTests(BudgetsInUsdFixture fixture) : IClassFixture<BudgetsInUsdFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Budget_limits_are_converted_at_the_rate_in_effect_now()
    {
        await using var db = TestPostgres.Context(fixture.WithRates);

        (await db.Budgets.AsNoTracking().SingleAsync(b => b.Id == BudgetsInUsdFixture.BudgetId, Ct)).LimitUsd.ShouldBe(100m); // 1 200 SEK at 12
    }

    [Fact]
    public async Task Alerts_are_converted_at_the_rate_in_effect_when_they_were_raised()
    {
        await using var db = TestPostgres.Context(fixture.WithRates);
        var alert = await db.AlertEvents.AsNoTracking().SingleAsync(a => a.Id == BudgetsInUsdFixture.AlertId, Ct);

        (alert.SpentUsd, alert.LimitUsd).ShouldBe((50m, 100m)); // 500 and 1 000 SEK at 10, the rate 10 days ago
    }

    [Fact]
    public async Task Without_an_exchange_rate_the_gateway_fallback_rate_of_10_is_used()
    {
        await using var db = TestPostgres.Context(fixture.WithoutRates);

        (await db.Budgets.AsNoTracking().SingleAsync(b => b.Id == BudgetsInUsdFixture.BudgetId, Ct)).LimitUsd.ShouldBe(120m);
    }

    [Fact]
    public async Task Reverting_the_migration_restores_the_SEK_amounts()
    {
        await using (var db = TestPostgres.Context(fixture.Reverted))
        {
            await db.GetService<IMigrator>().MigrateAsync(BudgetsInUsdFixture.LastBeforeBudgetsInUsd, Ct);
        }

        await using var connection = new NpgsqlConnection(fixture.Reverted);
        await connection.OpenAsync(Ct);
        await using var budget = new NpgsqlCommand("""SELECT "LimitSek" FROM "Budgets" """, connection);
        ((decimal)(await budget.ExecuteScalarAsync(Ct))!).ShouldBe(1200m);
        await using var alert = new NpgsqlCommand("""SELECT "SpentSek", "LimitSek" FROM "AlertEvents" """, connection);
        await using var reader = await alert.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        (reader.GetDecimal(0), reader.GetDecimal(1)).ShouldBe((500m, 1000m));
    }
}

/// <summary>
/// Three databases migrated to the last migration before <c>BudgetsInUsd</c>, given a budget of 1 200 SEK and an alert
/// raised 10 days ago (500 of 1 000 SEK), then migrated to the current version. <c>with-rates</c> has had 10 SEK per USD
/// since 30 days ago and 12 since yesterday; <c>without-rates</c> has no rate; <c>reverted</c> is <c>with-rates</c> again,
/// for a test that migrates it back down.
/// </summary>
public sealed class BudgetsInUsdFixture : IAsyncLifetime
{
    public const string LastBeforeBudgetsInUsd = "20261015051119_RequestLogSwitch";
    public static readonly Guid BudgetId = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
    public static readonly Guid AlertId = Guid.Parse("0199f0a0-0000-7000-8000-000000000002");

    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();

    public string WithRates => Database("with_rates");

    public string WithoutRates => Database("without_rates");

    public string Reverted => Database("reverted");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        foreach (var (connectionString, rates) in new[] { (WithRates, true), (WithoutRates, false), (Reverted, true) })
        {
            var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
            await ExecuteAsync(_postgres.GetConnectionString(), $"CREATE DATABASE {name}");
            await using (var db = TestPostgres.Context(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastBeforeBudgetsInUsd);
            }

            await ExecuteAsync(connectionString, SekRows(rates));
            await TestPostgres.MigrateAsync(connectionString);
        }
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private string Database(string name) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;

    private static string SekRows(bool rates) => (rates
        ? """
          INSERT INTO "ExchangeRates" ("Id", "Currency", "SekPerUnit", "EffectiveFrom") VALUES
              ('0199f0a0-0000-7000-8000-000000000003', 'USD', 10, now() - interval '30 days'),
              ('0199f0a0-0000-7000-8000-000000000004', 'USD', 12, now() - interval '1 day');
          """
        : string.Empty) + $$"""
        INSERT INTO "Budgets" ("Id", "Scope", "ScopeId", "LimitSek", "Period", "AlertThresholds", "IsActive", "CreatedAt")
            VALUES ('{{BudgetId}}', 'Team', gen_random_uuid(), 1200, 'Monthly', '{50,80,100}', true, now());
        INSERT INTO "AlertEvents" ("Id", "BudgetId", "Scope", "ScopeId", "PeriodStart", "ThresholdPercent", "SpentSek", "LimitSek", "Timestamp", "Acknowledged")
            VALUES ('{{AlertId}}', '{{BudgetId}}', 'Team', gen_random_uuid(), now() - interval '12 days', 50, 500, 1000, now() - interval '10 days', false);
        """;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
