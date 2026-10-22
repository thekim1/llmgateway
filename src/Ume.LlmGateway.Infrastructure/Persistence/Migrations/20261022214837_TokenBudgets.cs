using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TokenBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LimitTokens",
                table: "Budgets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Residency",
                table: "Budgets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "Budgets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Usd");

            migrationBuilder.AddColumn<long>(
                name: "LimitTokens",
                table: "AlertEvents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpentTokens",
                table: "AlertEvents",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LimitTokens",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "Residency",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "LimitTokens",
                table: "AlertEvents");

            migrationBuilder.DropColumn(
                name: "SpentTokens",
                table: "AlertEvents");
        }
    }
}
