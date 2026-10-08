using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Features",
                table: "ModelDeployments",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Features",
                table: "ModelDeployments");
        }
    }
}
