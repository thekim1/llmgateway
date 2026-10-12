using Npgsql;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>
/// The production database roles (deploy/postgres/init.sql and app-roles.sql) against the real schema: the services get only
/// what they need. Here for what the gateway-wide settings and data retention added.
/// </summary>
public sealed class DatabaseRoleTests(DatabaseRolesFixture fixture) : IClassFixture<DatabaseRolesFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Connection(string user) => fixture.Connection(user);

    [Fact]
    public async Task Admin_role_removes_old_rows_only_through_the_retention_function()
    {
        var admin = Connection("ume_admin");
        (await ScalarAsync<long>(admin, "SELECT ume_retention_step(100)")).ShouldBeGreaterThanOrEqualTo(0);
        (await ScalarAsync<DateTime?>(admin, """SELECT "UsageRetainedFrom" FROM "GatewaySettings" """)).ShouldNotBeNull();

        await ShouldBeDeniedAsync(admin, """DELETE FROM "AuditLog" """);
        await ShouldBeDeniedAsync(admin, """DELETE FROM "UsageRecords" """);
        await ShouldBeDeniedAsync(admin, """DELETE FROM "UsageMonthlyTotals" """);
        await ShouldBeDeniedAsync(admin, """
            INSERT INTO "UsageMonthlyTotals" ("MonthStart", "VirtualKeyId", "TeamId", "DepartmentId", "Endpoint", "RequestedModel", "Requests",
                "SuccessfulRequests", "Fallbacks", "InputTokens", "CachedInputTokens", "OutputTokens", "AudioSeconds", "CostUsd", "CostSek")
            VALUES (now(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'ChatCompletions', 'm', 1, 1, 0, 0, 0, 0, 0, 0, 0)
            """);
        // Its own settings it may change.
        await ExecuteAsync(admin, """UPDATE "GatewaySettings" SET "UsageRetentionDays" = 400, "TrustedProxies" = '{10.0.0.5}' """);
    }

    [Fact]
    public async Task Other_roles_cannot_run_the_retention_function()
    {
        await ShouldBeDeniedAsync(Connection("ume_gateway"), "SELECT ume_retention_step(100)");
        await ShouldBeDeniedAsync(Connection("ume_data"), "SELECT ume_retention_step(100)");
    }

    [Fact]
    public async Task Gateway_reads_settings_and_key_address_lists()
    {
        var gateway = Connection("ume_gateway");
        await ScalarAsync<long>(gateway, """SELECT count(*) FROM "GatewaySettings" """);
        await ScalarAsync<long>(gateway, """SELECT count("AllowedIpRanges") FROM "VirtualKeys" """);
        await ShouldBeDeniedAsync(gateway, """UPDATE "GatewaySettings" SET "TrustedProxies" = '{}' """);
    }

    [Fact]
    public async Task Data_api_reads_monthly_totals_and_the_retention_boundary_only()
    {
        var data = Connection("ume_data");
        await ScalarAsync<long>(data, """SELECT count(*) FROM "UsageMonthlyTotals" """);
        await ScalarAsync<long>(data, """SELECT count("UsageRetainedFrom") FROM "GatewaySettings" """);
        await ScalarAsync<long>(data, """SELECT count("AllowedIpRanges") FROM "VirtualKeys" """);
        await ShouldBeDeniedAsync(data, """SELECT "TrustedProxies" FROM "GatewaySettings" """);
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull or null ? default! : (T)value;
    }

    private static async Task ShouldBeDeniedAsync(string connectionString, string sql)
    {
        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}

/// <summary>One database with the production roles (init.sql without its secret files, then app-roles.sql) for all tests.</summary>
public sealed class DatabaseRolesFixture : IAsyncLifetime
{
    private const string Password = "role-test-password";
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await DatabaseRoleTests.ExecuteAsync(_postgres.GetConnectionString(), $"""
            CREATE ROLE ume_migrator LOGIN NOSUPERUSER PASSWORD '{Password}';
            CREATE ROLE ume_gateway LOGIN NOSUPERUSER PASSWORD '{Password}';
            CREATE ROLE ume_admin LOGIN NOSUPERUSER PASSWORD '{Password}';
            CREATE ROLE ume_data LOGIN NOSUPERUSER PASSWORD '{Password}';
            """);
        await DatabaseRoleTests.ExecuteAsync(_postgres.GetConnectionString(), "CREATE DATABASE gatewaydb OWNER ume_migrator");
        await DatabaseRoleTests.ExecuteAsync(Connection(null), """
            REVOKE ALL ON DATABASE gatewaydb FROM PUBLIC;
            GRANT CONNECT ON DATABASE gatewaydb TO ume_gateway, ume_admin;
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            ALTER SCHEMA public OWNER TO ume_migrator;
            GRANT USAGE ON SCHEMA public TO ume_gateway, ume_admin;
            """);
        await TestPostgres.MigrateAsync(Connection("ume_migrator"));
        await DatabaseRoleTests.ExecuteAsync(Connection("ume_migrator"), await File.ReadAllTextAsync(RolesSqlPath()));
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    public string Connection(string? user)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "gatewaydb" };
        if (user is not null)
        {
            builder.Username = user;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }

    private static string RolesSqlPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "deploy", "postgres", "app-roles.sql");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("deploy/postgres/app-roles.sql not found above the test output directory.");
    }
}
