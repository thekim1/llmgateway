using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelListPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoUpdatePrices",
                table: "ProviderAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "ModelPrices",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoUpdatePrice",
                table: "ModelDeployments",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ModelDeployments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoUpdatePrices",
                table: "ProviderAccounts");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ModelPrices");

            migrationBuilder.DropColumn(
                name: "AutoUpdatePrice",
                table: "ModelDeployments");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "ModelDeployments");
        }
    }
}
