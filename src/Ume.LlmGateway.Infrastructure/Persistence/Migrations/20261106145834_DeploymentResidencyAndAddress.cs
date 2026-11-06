using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentResidencyAndAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                table: "ModelDeployments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestAuthMode",
                table: "ModelDeployments",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestBaseUrl",
                table: "ModelDeployments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Residency",
                table: "ModelDeployments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Publisher",
                table: "ModelDeployments");

            migrationBuilder.DropColumn(
                name: "RequestAuthMode",
                table: "ModelDeployments");

            migrationBuilder.DropColumn(
                name: "RequestBaseUrl",
                table: "ModelDeployments");

            migrationBuilder.DropColumn(
                name: "Residency",
                table: "ModelDeployments");
        }
    }
}
