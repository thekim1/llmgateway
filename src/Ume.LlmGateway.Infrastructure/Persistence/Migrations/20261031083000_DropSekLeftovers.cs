using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropSekLeftovers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The retention function summed CostSek into the monthly totals; without this it would fail once the columns are gone.
            migrationBuilder.Sql(RetentionStepFunction);

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropColumn(
                name: "CostSek",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "CostSek",
                table: "UsageMonthlyTotals");
        }

        /// <summary>
        /// <see cref="DataRetention.RetentionStepFunction"/> without <c>CostSek</c>. <c>CREATE OR REPLACE</c> keeps the
        /// function's owner and grants (only <c>ume_admin</c> may execute it).
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
                        "InputTokens", "CachedInputTokens", "OutputTokens", "AudioSeconds", "CostUsd")
                    SELECT date_trunc('month', u."Timestamp" AT TIME ZONE 'Europe/Stockholm') AT TIME ZONE 'Europe/Stockholm',
                           u."VirtualKeyId", u."TeamId", u."DepartmentId", u."Endpoint", u."RequestedModel",
                           u."ProviderAccountId", u."ProviderName", u."ModelDeploymentId",
                           count(*), count(*) FILTER (WHERE u."Outcome" = 'Success'), COALESCE(sum(u."FallbackCount"), 0),
                           sum(u."InputTokens"), sum(u."CachedInputTokens"), sum(u."OutputTokens"),
                           sum(u."AudioSeconds"), sum(u."CostUsd")
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
            migrationBuilder.AddColumn<decimal>(
                name: "CostSek",
                table: "UsageRecords",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CostSek",
                table: "UsageMonthlyTotals",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SekPerUnit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_Currency_EffectiveFrom",
                table: "ExchangeRates",
                columns: new[] { "Currency", "EffectiveFrom" },
                unique: true);

            migrationBuilder.Sql(DataRetention.RetentionStepFunction);
        }
    }
}
