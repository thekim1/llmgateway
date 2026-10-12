using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImageGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PerImageUsd",
                table: "ModelPrices",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerImageUsd",
                table: "ModelPrices");
        }
    }
}
