using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditRetentionDays",
                table: "GatewaySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionLastRunAt",
                table: "GatewaySettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RetentionLastRunDeleted",
                table: "GatewaySettings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetentionLastRunError",
                table: "GatewaySettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UsageRetainedFrom",
                table: "GatewaySettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsageRetentionDays",
                table: "GatewaySettings",
                type: "integer",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.CreateTable(
                name: "UsageMonthlyTotals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MonthStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VirtualKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestedModel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ModelDeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    SuccessfulRequests = table.Column<long>(type: "bigint", nullable: false),
                    Fallbacks = table.Column<long>(type: "bigint", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    AudioSeconds = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    CostSek = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageMonthlyTotals", x => x.Id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_GatewaySettings_AuditRetention",
                table: "GatewaySettings",
                sql: "\"AuditRetentionDays\" IS NULL OR \"AuditRetentionDays\" BETWEEN 90 AND 3650");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GatewaySettings_UsageRetention",
                table: "GatewaySettings",
                sql: "\"UsageRetentionDays\" BETWEEN 90 AND 3650");

            migrationBuilder.CreateIndex(
                name: "IX_UsageMonthlyTotals_MonthStart_DepartmentId",
                table: "UsageMonthlyTotals",
                columns: new[] { "MonthStart", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageMonthlyTotals_TeamId_MonthStart",
                table: "UsageMonthlyTotals",
                columns: new[] { "TeamId", "MonthStart" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageMonthlyTotals_VirtualKeyId_MonthStart",
                table: "UsageMonthlyTotals",
                columns: new[] { "VirtualKeyId", "MonthStart" });

            // Existing settings rows keep the audit log for the default 365 days; null (set later in the admin UI) keeps it for ever.
            migrationBuilder.Sql("""UPDATE "GatewaySettings" SET "AuditRetentionDays" = 365;""");

            // The nightly retention job (admin API, RetentionService) calls this repeatedly until it returns 0. It runs with the
            // owner's rights, so the admin role can remove old rows without DELETE on these tables: the audit log stays
            // append-only for everything younger than the retention, and nobody but this function writes monthly totals.
            migrationBuilder.Sql(RetentionStepFunction);
            migrationBuilder.Sql("""REVOKE ALL ON FUNCTION ume_retention_step(integer) FROM PUBLIC;""");
        }

        /// <summary>
        /// One step: saves monthly totals for whole Europe/Stockholm months that fell out of the usage retention (once per
        /// month, in the same transaction that moves <c>UsageRetainedFrom</c>), then removes at most <c>batch_size</c> rows
        /// older than the retention from UsageRecords, AuthFailures, AlertEvents (only of periods that ended over a year ago)
        /// and AuditLog. Returns the number removed. Retention below 90 days is treated as 90, whatever the settings say.
        /// </summary>
        internal const string RetentionStepFunction = """
            CREATE OR REPLACE FUNCTION ume_retention_step(batch_size integer) RETURNS bigint
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
            DECLARE
                settings "GatewaySettings"%ROWTYPE;
                size integer := LEAST(GREATEST(COALESCE(batch_size, 1000), 1), 50000);
                cutoff timestamptz;
                removed bigint := 0;
                n bigint;
            BEGIN
                INSERT INTO "GatewaySettings" ("Id", "TrustedProxies", "AuditRetentionDays") VALUES (1, '{}', 365) ON CONFLICT ("Id") DO NOTHING;
                -- Also serialises concurrent runs (several admin API instances).
                SELECT * INTO settings FROM "GatewaySettings" WHERE "Id" = 1 FOR UPDATE;
                cutoff := date_trunc('month', (now() - make_interval(days => GREATEST(settings."UsageRetentionDays", 90))) AT TIME ZONE 'Europe/Stockholm')
                          AT TIME ZONE 'Europe/Stockholm';

                IF settings."UsageRetainedFrom" IS NULL OR settings."UsageRetainedFrom" < cutoff THEN
                    INSERT INTO "UsageMonthlyTotals" ("MonthStart", "VirtualKeyId", "TeamId", "DepartmentId", "Endpoint", "RequestedModel",
                        "ProviderAccountId", "ProviderName", "ModelDeploymentId", "Requests", "SuccessfulRequests", "Fallbacks",
                        "InputTokens", "CachedInputTokens", "OutputTokens", "AudioSeconds", "CostUsd", "CostSek")
                    SELECT date_trunc('month', u."Timestamp" AT TIME ZONE 'Europe/Stockholm') AT TIME ZONE 'Europe/Stockholm',
                           u."VirtualKeyId", u."TeamId", u."DepartmentId", u."Endpoint", u."RequestedModel",
                           u."ProviderAccountId", u."ProviderName", u."ModelDeploymentId",
                           count(*), count(*) FILTER (WHERE u."Outcome" = 'Success'), COALESCE(sum(u."FallbackCount"), 0),
                           sum(u."InputTokens"), sum(u."CachedInputTokens"), sum(u."OutputTokens"),
                           sum(u."AudioSeconds"), sum(u."CostUsd"), sum(u."CostSek")
                    FROM "UsageRecords" u
                    WHERE u."Timestamp" < cutoff AND (settings."UsageRetainedFrom" IS NULL OR u."Timestamp" >= settings."UsageRetainedFrom")
                    GROUP BY 1, 2, 3, 4, 5, 6, 7, 8, 9;
                    UPDATE "GatewaySettings" SET "UsageRetainedFrom" = cutoff WHERE "Id" = 1;
                    settings."UsageRetainedFrom" := cutoff;
                END IF;

                -- Records before UsageRetainedFrom are already in the totals, and readers ignore them, so batches may lag.
                DELETE FROM "UsageRecords" WHERE ctid IN (
                    SELECT ctid FROM "UsageRecords" WHERE "Timestamp" < settings."UsageRetainedFrom" LIMIT size - removed);
                GET DIAGNOSTICS n = ROW_COUNT; removed := removed + n;
                IF removed >= size THEN RETURN removed; END IF;

                DELETE FROM "AuthFailures" WHERE ctid IN (
                    SELECT ctid FROM "AuthFailures" WHERE "LastSeen" < cutoff LIMIT size - removed);
                GET DIAGNOSTICS n = ROW_COUNT; removed := removed + n;
                IF removed >= size THEN RETURN removed; END IF;

                -- An alert of a period still running stops the gateway from raising the same threshold again.
                DELETE FROM "AlertEvents" WHERE ctid IN (
                    SELECT ctid FROM "AlertEvents" WHERE "Timestamp" < cutoff AND "PeriodStart" < now() - interval '13 months' LIMIT size - removed);
                GET DIAGNOSTICS n = ROW_COUNT; removed := removed + n;
                IF removed >= size THEN RETURN removed; END IF;

                IF settings."AuditRetentionDays" IS NOT NULL THEN
                    DELETE FROM "AuditLog" WHERE ctid IN (
                        SELECT ctid FROM "AuditLog" WHERE "Timestamp" < now() - make_interval(days => GREATEST(settings."AuditRetentionDays", 90))
                        LIMIT size - removed);
                    GET DIAGNOSTICS n = ROW_COUNT; removed := removed + n;
                END IF;

                RETURN removed;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP FUNCTION IF EXISTS ume_retention_step(integer);""");

            migrationBuilder.DropTable(
                name: "UsageMonthlyTotals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GatewaySettings_AuditRetention",
                table: "GatewaySettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GatewaySettings_UsageRetention",
                table: "GatewaySettings");

            migrationBuilder.DropColumn(
                name: "AuditRetentionDays",
                table: "GatewaySettings");

            migrationBuilder.DropColumn(
                name: "RetentionLastRunAt",
                table: "GatewaySettings");

            migrationBuilder.DropColumn(
                name: "RetentionLastRunDeleted",
                table: "GatewaySettings");

            migrationBuilder.DropColumn(
                name: "RetentionLastRunError",
                table: "GatewaySettings");

            migrationBuilder.DropColumn(
                name: "UsageRetainedFrom",
                table: "GatewaySettings");

            migrationBuilder.DropColumn(
                name: "UsageRetentionDays",
                table: "GatewaySettings");
        }
    }
}
