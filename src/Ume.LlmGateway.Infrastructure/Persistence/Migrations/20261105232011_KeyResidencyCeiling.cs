using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KeyResidencyCeiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireMaxResidency",
                table: "VirtualKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireMaxResidency",
                table: "VirtualKeys");
        }
    }
}
