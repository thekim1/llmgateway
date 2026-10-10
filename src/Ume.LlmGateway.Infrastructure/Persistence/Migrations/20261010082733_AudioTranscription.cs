using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AudioTranscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AudioSeconds",
                table: "UsageRecords",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AudioPerMinuteUsd",
                table: "ModelPrices",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(
                name: "AudioInputPerMillionUsd",
                table: "ModelPrices",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AudioOutputPerMillionUsd",
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
                name: "AudioSeconds",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "AudioPerMinuteUsd",
                table: "ModelPrices");

            migrationBuilder.DropColumn(
                name: "AudioInputPerMillionUsd",
                table: "ModelPrices");

            migrationBuilder.DropColumn(
                name: "AudioOutputPerMillionUsd",
                table: "ModelPrices");
        }
    }
}
