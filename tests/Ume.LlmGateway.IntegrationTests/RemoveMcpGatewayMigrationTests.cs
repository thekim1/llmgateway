using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>
/// Upgrade from 0.4.0 to 0.4.1: migration <c>RemoveMcpGateway</c> on a database that ran the reverted migration
/// <c>McpGateway</c> and recorded MCP traffic must end with the schema of a new install and rows EF Core can read.
/// </summary>
public sealed class RemoveMcpGatewayMigrationTests(McpUpgradeFixture fixture) : IClassFixture<McpUpgradeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rows_of_the_removed_endpoints_are_deleted_and_the_rest_can_be_read()
    {
        await using var db = TestPostgres.Context(fixture.Upgraded);

        (await db.UsageRecords.AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem().Endpoint.ShouldBe(GatewayEndpoint.ChatCompletions);
        (await db.UsageMonthlyTotals.AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem().Endpoint.ShouldBe(GatewayEndpoint.ChatCompletions);
        (await db.AuthFailures.AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem().Endpoint.ShouldBe(GatewayEndpoint.ChatCompletions);
        (await db.VirtualKeys.AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem().Name.ShouldBe("upgrade-test");
    }

    [Fact]
    public async Task History_ends_at_the_new_migration_without_the_reverted_one()
    {
        await using var db = TestPostgres.Context(fixture.Upgraded);
        var applied = (await db.Database.GetAppliedMigrationsAsync(Ct)).ToList();

        applied.ShouldNotContain(McpUpgradeFixture.McpGateway);
        applied.ShouldBe(db.Database.GetMigrations().ToList());
        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Upgraded_schema_equals_a_new_install()
    {
        var upgraded = await SchemaAsync(fixture.Upgraded);

        upgraded.ShouldNotContain(line => line.Contains("mcp", StringComparison.OrdinalIgnoreCase) || line.Contains("ParentRequestId", StringComparison.Ordinal));
        upgraded.ShouldBe(await SchemaAsync(fixture.Fresh));
    }

    /// <summary>Columns, indexes and constraints of the public schema, one sorted line each.</summary>
    private static async Task<List<string>> SchemaAsync(string connectionString)
    {
        const string sql = """
            SELECT 'column ' || table_name || '.' || column_name || ' ' || data_type || ' ' || coalesce(character_maximum_length::text, '')
                   || ' ' || is_nullable || ' ' || coalesce(column_default, '')
              FROM information_schema.columns WHERE table_schema = 'public'
            UNION ALL SELECT 'index ' || indexdef FROM pg_indexes WHERE schemaname = 'public'
            UNION ALL SELECT 'constraint ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
              FROM pg_constraint WHERE connamespace = 'public'::regnamespace
            ORDER BY 1
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}

/// <summary>
/// Two databases in one container: <c>upgraded</c> is migrated to 0.4.0 (the last migration before <c>McpGateway</c>, then
/// that migration's script exactly as 0.4.0 generated it), given MCP rows, then migrated to the current version;
/// <c>fresh</c> is a new install of the current version.
/// </summary>
public sealed class McpUpgradeFixture : IAsyncLifetime
{
    public const string McpGateway = "20261012233443_McpGateway";
    private const string LastBeforeMcpGateway = "20261012224439_RequestTimings";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();

    public string Upgraded => Database("upgraded");

    public string Fresh => Database("fresh");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await ExecuteAsync(_postgres.GetConnectionString(), "CREATE DATABASE upgraded");
        await ExecuteAsync(_postgres.GetConnectionString(), "CREATE DATABASE fresh");

        await using (var db = TestPostgres.Context(Upgraded))
        {
            await db.GetService<IMigrator>().MigrateAsync(LastBeforeMcpGateway);
        }

        await ExecuteAsync(Upgraded, McpGatewayScript);
        await ExecuteAsync(Upgraded, McpTraffic);
        await TestPostgres.MigrateAsync(Upgraded);
        await TestPostgres.MigrateAsync(Fresh);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private string Database(string name) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>What 0.4.0 recorded: a key with an MCP limit and grant, and usage, totals and refused keys on the MCP endpoints.</summary>
    private const string McpTraffic = """
        INSERT INTO "Departments" ("Id", "Name", "CostCenterCode", "IsActive", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-00000000d001', 'Dept', 'CC1', true, now());
        INSERT INTO "Teams" ("Id", "DepartmentId", "Name", "IsActive", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-00000000b001', '00000000-0000-0000-0000-00000000d001', 'Team', true, now());
        INSERT INTO "VirtualKeys" ("Id", "TeamId", "Name", "Prefix", "KeyHash", "IsEnabled", "CreatedAt", "AllowedModels",
                "AllowedResidencies", "PiiPolicy", "McpCallsPerMinute")
            VALUES ('00000000-0000-0000-0000-00000000a001', '00000000-0000-0000-0000-00000000b001', 'upgrade-test', 'ume-up',
                'hash', true, now(), '{}', '{0}', 'Redact', 30);
        INSERT INTO "McpServers" ("Id", "Slug", "DisplayName", "Url", "Residency", "IsEnabled", "AuthType", "ForwardHeaders",
                "TimeoutSeconds", "MaxResultBytes", "AutoApproveNewTools", "Status", "Capabilities", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-00000000c001', 'demo', 'Demo', 'https://mcp.example/mcp', 'Sweden', true, 'None',
                '{}', 30, 1048576, false, 'Healthy', 1, now());
        INSERT INTO "McpTools" ("Id", "McpServerId", "Name", "DefinitionHash", "Definition", "ApprovalState", "ExposedName", "DiscoveredAt")
            VALUES (gen_random_uuid(), '00000000-0000-0000-0000-00000000c001', 'echo', 'h', '{}', 'Approved', 'demo__echo', now());
        INSERT INTO "VirtualKeyMcpGrants" ("Id", "VirtualKeyId", "McpServerId", "AllowedTools", "ApprovalPolicy", "AllowResources",
                "AllowPrompts", "CreatedAt")
            VALUES (gen_random_uuid(), '00000000-0000-0000-0000-00000000a001', '00000000-0000-0000-0000-00000000c001', '{*}', 'Never',
                false, false, now());
        INSERT INTO "UsageRecords" ("RequestId", "Timestamp", "VirtualKeyId", "TeamId", "DepartmentId", "Endpoint", "RequestedModel",
                "InputTokens", "CachedInputTokens", "OutputTokens", "CostUsd", "CostSek", "LatencyMs", "StatusCode", "Outcome",
                "FallbackCount", "Streamed", "McpServerId", "McpMethod", "McpTool", "ParentRequestId")
            SELECT r.id, now(), '00000000-0000-0000-0000-00000000a001', '00000000-0000-0000-0000-00000000b001',
                   '00000000-0000-0000-0000-00000000d001', r.endpoint, r.model, 10, 0, 5, 0.01, 0.1, 100, 200, 'Success', 0, false,
                   r.server, r.method, r.tool, r.parent
              FROM (VALUES
                ('req-llm', 'ChatCompletions', 'gpt-test', NULL::uuid, NULL, NULL, NULL),
                ('req-mcp', 'Mcp', 'mcp:demo/echo', '00000000-0000-0000-0000-00000000c001'::uuid, 'tools/call', 'echo', NULL),
                ('req-tool', 'McpToolCall', 'mcp:demo/echo', '00000000-0000-0000-0000-00000000c001'::uuid, 'tools/call', 'echo', 'req-llm'))
                AS r(id, endpoint, model, server, method, tool, parent);
        INSERT INTO "UsageMonthlyTotals" ("MonthStart", "VirtualKeyId", "TeamId", "DepartmentId", "Endpoint", "RequestedModel", "Requests",
                "SuccessfulRequests", "Fallbacks", "InputTokens", "CachedInputTokens", "OutputTokens", "AudioSeconds", "CostUsd", "CostSek")
            SELECT date_trunc('month', now()), '00000000-0000-0000-0000-00000000a001', '00000000-0000-0000-0000-00000000b001',
                   '00000000-0000-0000-0000-00000000d001', e, 'm', 1, 1, 0, 0, 0, 0, 0, 0, 0
              FROM unnest(ARRAY['ChatCompletions', 'Mcp', 'McpToolCall']) AS e;
        INSERT INTO "AuthFailures" ("FirstSeen", "LastSeen", "Reason", "Endpoint", "Count")
            SELECT now(), now(), 'InvalidKey', e, 1 FROM unnest(ARRAY['ChatCompletions', 'Mcp', 'McpToolCall']) AS e;
        """;

    /// <summary>The script of migration <c>McpGateway</c> as 0.4.0 generated it (<c>IMigrator.GenerateScript</c>), unchanged.</summary>
    private const string McpGatewayScript = """
        START TRANSACTION;
        ALTER TABLE "VirtualKeys" ADD "McpCallsPerMinute" integer;

        ALTER TABLE "UsageRecords" ADD "McpMethod" character varying(50);

        ALTER TABLE "UsageRecords" ADD "McpServerId" uuid;

        ALTER TABLE "UsageRecords" ADD "McpTool" character varying(128);

        ALTER TABLE "UsageRecords" ADD "ParentRequestId" character varying(64);

        CREATE TABLE "McpServers" (
            "Id" uuid NOT NULL,
            "Slug" character varying(24) NOT NULL,
            "DisplayName" character varying(200) NOT NULL,
            "Description" character varying(1000),
            "Url" character varying(500) NOT NULL,
            "Residency" character varying(20) NOT NULL,
            "IsEnabled" boolean NOT NULL,
            "OwnerDepartmentId" uuid,
            "AuthType" character varying(30) NOT NULL,
            "EncryptedCredential" character varying(4000),
            "ForwardHeaders" text[] NOT NULL,
            "TimeoutSeconds" integer NOT NULL,
            "MaxResultBytes" integer NOT NULL,
            "AutoApproveNewTools" boolean NOT NULL,
            "Status" character varying(20) NOT NULL,
            "LastCheckedAt" timestamp with time zone,
            "LastError" character varying(1000),
            "Capabilities" integer NOT NULL,
            "Instructions" character varying(4096),
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_McpServers" PRIMARY KEY ("Id")
        );

        CREATE TABLE "McpTools" (
            "Id" uuid NOT NULL,
            "McpServerId" uuid NOT NULL,
            "Name" character varying(128) NOT NULL,
            "Title" character varying(200),
            "DefinitionHash" character varying(64) NOT NULL,
            "Definition" jsonb NOT NULL,
            "ApprovalState" character varying(20) NOT NULL,
            "ApprovedDefinition" jsonb,
            "ExposedName" character varying(64) NOT NULL,
            "PriceSek" numeric(18,6),
            "DiscoveredAt" timestamp with time zone NOT NULL,
            "ApprovedAt" timestamp with time zone,
            "ApprovedBy" character varying(200),
            CONSTRAINT "PK_McpTools" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_McpTools_McpServers_McpServerId" FOREIGN KEY ("McpServerId") REFERENCES "McpServers" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE "VirtualKeyMcpGrants" (
            "Id" uuid NOT NULL,
            "VirtualKeyId" uuid NOT NULL,
            "McpServerId" uuid NOT NULL,
            "AllowedTools" text[] NOT NULL,
            "ApprovalPolicy" character varying(20) NOT NULL,
            "AllowResources" boolean NOT NULL,
            "AllowPrompts" boolean NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_VirtualKeyMcpGrants" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_VirtualKeyMcpGrants_McpServers_McpServerId" FOREIGN KEY ("McpServerId") REFERENCES "McpServers" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_VirtualKeyMcpGrants_VirtualKeys_VirtualKeyId" FOREIGN KEY ("VirtualKeyId") REFERENCES "VirtualKeys" ("Id") ON DELETE CASCADE
        );

        CREATE INDEX "IX_UsageRecords_McpServerId_Timestamp" ON "UsageRecords" ("McpServerId", "Timestamp") WHERE "McpServerId" IS NOT NULL;

        CREATE UNIQUE INDEX "IX_McpServers_Slug" ON "McpServers" ("Slug");

        CREATE UNIQUE INDEX "IX_McpTools_ExposedName" ON "McpTools" ("ExposedName");

        CREATE UNIQUE INDEX "IX_McpTools_McpServerId_Name" ON "McpTools" ("McpServerId", "Name");

        CREATE INDEX "IX_VirtualKeyMcpGrants_McpServerId" ON "VirtualKeyMcpGrants" ("McpServerId");

        CREATE UNIQUE INDEX "IX_VirtualKeyMcpGrants_VirtualKeyId_McpServerId" ON "VirtualKeyMcpGrants" ("VirtualKeyId", "McpServerId");

        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20261012233443_McpGateway', '10.0.12');

        COMMIT;
        """;
}
