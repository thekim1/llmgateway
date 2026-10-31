using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>
/// The production database roles (deploy/postgres/init.sql and app-roles.sql) against the real schema: the services get only
/// what they need. Here for what the gateway-wide settings and data retention added.
/// </summary>
public sealed class DatabaseRoleTests(DatabaseRolesFixture fixture) : IClassFixture<DatabaseRolesFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Connection(string? user) => fixture.Connection(user);

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
                "SuccessfulRequests", "Fallbacks", "InputTokens", "CachedInputTokens", "OutputTokens", "AudioSeconds", "CostUsd")
            VALUES (now(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'ChatCompletions', 'm', 1, 1, 0, 0, 0, 0, 0, 0)
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
    public async Task Gateway_writes_usage_with_copy_and_sets_last_used()
    {
        await using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(Connection("ume_gateway")).Options);
        var requestId = "req_" + Guid.NewGuid().ToString("N");
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await db.CopyUsageRecordsAsync(
                [new UsageRecord { RequestId = requestId, Timestamp = DateTimeOffset.UtcNow, RequestedModel = "m", StatusCode = 200 }], Ct);
            await db.UpdateKeysLastUsedAsync([(Guid.NewGuid(), DateTimeOffset.UtcNow)], Ct);
            await transaction.CommitAsync(Ct);
        }

        (await ScalarAsync<long>(Connection("ume_gateway"), $"""SELECT count(*) FROM "UsageRecords" WHERE "RequestId" = '{requestId}' """)).ShouldBe(1);
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

    [Fact]
    public async Task Each_role_reads_and_writes_exactly_the_tables_it_needs()
    {
        // A new table must be added to app-roles.sql on purpose: until then no service can read it and this test fails.
        string[] all = [.. await ListAsync(Connection(null), """
            SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1
            """)];
        string[] notForGateway = ["AuditLog", "DataApiKeys", "__EFMigrationsHistory"];

        (await PrivilegesAsync("ume_gateway", "SELECT")).ShouldBe([.. all.Except(notForGateway)], ignoreOrder: true);
        (await PrivilegesAsync("ume_gateway", "INSERT")).ShouldBe(["AlertEvents", "AuthFailures", "UsageRecords"], ignoreOrder: true);
        (await PrivilegesAsync("ume_gateway", "UPDATE")).ShouldBeEmpty();
        (await PrivilegesAsync("ume_gateway", "DELETE")).ShouldBeEmpty();

        (await PrivilegesAsync("ume_admin", "SELECT")).ShouldBe(all, ignoreOrder: true);
        (await PrivilegesAsync("ume_admin", "INSERT")).ShouldBe([.. Configuration, "AuditLog", "DataProtectionKeys"], ignoreOrder: true);
        (await PrivilegesAsync("ume_admin", "UPDATE")).ShouldBe(Configuration, ignoreOrder: true);
        (await PrivilegesAsync("ume_admin", "DELETE")).ShouldBe(Configuration, ignoreOrder: true);
    }

    [Fact]
    public async Task Gateway_cannot_read_the_audit_log_or_data_api_keys_or_add_data_protection_keys()
    {
        var gateway = Connection("ume_gateway");
        await ShouldBeDeniedAsync(gateway, """SELECT count(*) FROM "AuditLog" """);
        await ShouldBeDeniedAsync(gateway, """SELECT count(*) FROM "DataApiKeys" """);
        await ShouldBeDeniedAsync(gateway, """INSERT INTO "DataProtectionKeys" ("FriendlyName", "Xml") VALUES ('k', '<key/>')""");
        await ShouldBeDeniedAsync(gateway, """UPDATE "VirtualKeys" SET "IsEnabled" = true""");
    }

    [Fact]
    public async Task Admin_role_cannot_move_the_retention_boundary()
    {
        var admin = Connection("ume_admin");
        await ShouldBeDeniedAsync(admin, """UPDATE "GatewaySettings" SET "UsageRetainedFrom" = now() - interval '10 years'""");
        await ShouldBeDeniedAsync(admin, """DELETE FROM "GatewaySettings" """);
        await ShouldBeDeniedAsync(admin, """INSERT INTO "GatewaySettings" ("Id", "TrustedProxies") VALUES (2, '{}')""");
        await ExecuteAsync(admin, """
            UPDATE "GatewaySettings" SET "LogAllRequests" = false, "LogAllRequestsUntil" = NULL, "UpdatedAt" = now(),
                "AuditRetentionDays" = 365, "RetentionLastRunAt" = now(), "RetentionLastRunDeleted" = 0, "RetentionLastRunError" = NULL
            """);
    }

    [Fact]
    public async Task Applying_the_roles_again_keeps_the_same_grants()
    {
        var before = await PrivilegesAsync("ume_gateway", "SELECT");
        await fixture.ApplyRolesAsync();
        (await PrivilegesAsync("ume_gateway", "SELECT")).ShouldBe(before, ignoreOrder: true);
        (await ScalarAsync<long>(Connection("ume_gateway"), """SELECT count(*) FROM "GatewaySettings" """)).ShouldBe(1);
    }

    [Fact]
    public async Task Gateway_decrypts_with_keys_the_admin_role_created()
    {
        await using var admin = DataProtection(Connection("ume_admin"), createKeys: true);
        var ciphertext = admin.GetRequiredService<IDataProtectionProvider>().CreateProtector("role-test").Protect("provider-secret");

        await using var gateway = DataProtection(Connection("ume_gateway"), createKeys: false);
        gateway.GetRequiredService<IDataProtectionProvider>().CreateProtector("role-test").Unprotect(ciphertext).ShouldBe("provider-secret");
    }

    private static readonly string[] Configuration = ["Departments", "Teams", "VirtualKeys", "DataApiKeys", "ProviderAccounts",
        "ModelDeployments", "ModelPrices", "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets", "Budgets"];

    private static ServiceProvider DataProtection(string connectionString, bool createKeys)
    {
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(o => o.UseNpgsql(connectionString));
        var dp = services.AddDataProtection().SetApplicationName(InfrastructureExtensions.DataProtectionApplicationName)
            .PersistKeysToDbContext<GatewayDbContext>();
        if (!createKeys)
        {
            dp.DisableAutomaticKeyGeneration();
        }

        return services.BuildServiceProvider();
    }

    /// <summary>Tables on which <paramref name="role"/> holds <paramref name="privilege"/> for the whole table.</summary>
    private async Task<List<string>> PrivilegesAsync(string role, string privilege) => await ListAsync(Connection(null), $"""
        SELECT table_name FROM information_schema.table_privileges
        WHERE table_schema = 'public' AND grantee = '{role}' AND privilege_type = '{privilege}'
        """);

    private static async Task<List<string>> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
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
        await ApplyRolesAsync();
    }

    /// <summary>Runs app-roles.sql as the migration service does (EF Core ExecuteSqlRaw, as the schema owner).</summary>
    public async Task ApplyRolesAsync()
    {
        await using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(Connection("ume_migrator")).Options);
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(RolesSqlPath()));
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
