using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderFeaturePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowProviderStorage",
                table: "VirtualKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowProviderTools",
                table: "VirtualKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowProviderStorage",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "AllowProviderTools",
                table: "VirtualKeys");
        }
    }
}
