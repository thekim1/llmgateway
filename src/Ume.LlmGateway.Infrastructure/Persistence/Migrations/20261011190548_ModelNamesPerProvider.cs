using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelNamesPerProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModelDeployments_Name",
                table: "ModelDeployments");

            migrationBuilder.DropIndex(
                name: "IX_ModelDeployments_ProviderAccountId",
                table: "ModelDeployments");

            migrationBuilder.CreateIndex(
                name: "IX_ModelDeployments_ProviderAccountId_Name",
                table: "ModelDeployments",
                columns: new[] { "ProviderAccountId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModelDeployments_ProviderAccountId_Name",
                table: "ModelDeployments");

            migrationBuilder.CreateIndex(
                name: "IX_ModelDeployments_Name",
                table: "ModelDeployments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelDeployments_ProviderAccountId",
                table: "ModelDeployments",
                column: "ProviderAccountId");
        }
    }
}
