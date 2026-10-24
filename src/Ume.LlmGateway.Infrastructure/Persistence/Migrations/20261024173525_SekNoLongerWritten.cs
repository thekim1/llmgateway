using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SekNoLongerWritten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0.8.1 no longer writes SEK. Expand step only: the columns get a default so new rows can leave them out, while
            // 0.8.0 instances still write them during a rolling update. They and ExchangeRates are dropped in the next release.
            migrationBuilder.AlterColumn<decimal>(
                name: "CostSek",
                table: "UsageRecords",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "CostSek",
                table: "UsageMonthlyTotals",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "CostSek",
                table: "UsageRecords",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "CostSek",
                table: "UsageMonthlyTotals",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6,
                oldDefaultValue: 0m);
        }
    }
}
