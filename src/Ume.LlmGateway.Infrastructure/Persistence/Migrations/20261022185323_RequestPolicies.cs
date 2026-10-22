using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequestPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestPolicy",
                table: "VirtualKeys",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestPolicy",
                table: "Teams",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestPolicy",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "RequestPolicy",
                table: "Teams");
        }
    }
}
