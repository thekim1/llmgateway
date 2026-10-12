using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequestTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TimeToFirstTokenMs",
                table: "UsageRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpstreamMs",
                table: "UsageRecords",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeToFirstTokenMs",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "UpstreamMs",
                table: "UsageRecords");
        }
    }
}
