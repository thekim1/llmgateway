using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BudgetsInUsd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LimitSek",
                table: "Budgets",
                newName: "LimitUsd");

            migrationBuilder.RenameColumn(
                name: "SpentSek",
                table: "AlertEvents",
                newName: "SpentUsd");

            migrationBuilder.RenameColumn(
                name: "LimitSek",
                table: "AlertEvents",
                newName: "LimitUsd");

            // Budget limits keep what they allowed just before the upgrade: converted at the USD rate in effect now (the
            // gateway's default fallback rate of 10 when none was entered). Stored alerts are converted at the rate in
            // effect when they were raised.
            migrationBuilder.Sql($"""
                UPDATE "Budgets" SET "LimitUsd" = round("LimitUsd" / {RateAt("now()")}, 2);
                UPDATE "AlertEvents" SET "SpentUsd" = round("SpentUsd" / {AlertRate}, 2), "LimitUsd" = round("LimitUsd" / {AlertRate}, 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE "Budgets" SET "LimitUsd" = round("LimitUsd" * {RateAt("now()")}, 2);
                UPDATE "AlertEvents" SET "SpentUsd" = round("SpentUsd" * {AlertRate}, 2), "LimitUsd" = round("LimitUsd" * {AlertRate}, 2);
                """);

            migrationBuilder.RenameColumn(
                name: "LimitUsd",
                table: "Budgets",
                newName: "LimitSek");

            migrationBuilder.RenameColumn(
                name: "SpentUsd",
                table: "AlertEvents",
                newName: "SpentSek");

            migrationBuilder.RenameColumn(
                name: "LimitUsd",
                table: "AlertEvents",
                newName: "LimitSek");
        }

        private static readonly string AlertRate = RateAt("\"AlertEvents\".\"Timestamp\"");

        /// <summary>SQL for SEK per USD at <paramref name="instant"/>: the rate then, else the one in effect now, else 10.</summary>
        private static string RateAt(string instant) => $"""
            COALESCE(
                (SELECT r."SekPerUnit" FROM "ExchangeRates" r WHERE r."Currency" = 'USD' AND r."EffectiveFrom" <= {instant} ORDER BY r."EffectiveFrom" DESC LIMIT 1),
                (SELECT r."SekPerUnit" FROM "ExchangeRates" r WHERE r."Currency" = 'USD' AND r."EffectiveFrom" <= now() ORDER BY r."EffectiveFrom" DESC LIMIT 1),
                10)
            """;
    }
}
