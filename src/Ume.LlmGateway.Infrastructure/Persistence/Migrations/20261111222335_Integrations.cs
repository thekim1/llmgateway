using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "VirtualKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationRole",
                table: "VirtualKeys",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<List<string>>(
                name: "IntegrationScopes",
                table: "VirtualKeys",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<int>(
                name: "MaxOutputTokens",
                table: "ModelDeployments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OwnerTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Integrations_Teams_OwnerTeamId",
                        column: x => x.OwnerTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VirtualKeys_IntegrationId",
                table: "VirtualKeys",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_Name",
                table: "Integrations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_OwnerTeamId",
                table: "Integrations",
                column: "OwnerTeamId");

            migrationBuilder.AddForeignKey(
                name: "FK_VirtualKeys_Integrations_IntegrationId",
                table: "VirtualKeys",
                column: "IntegrationId",
                principalTable: "Integrations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VirtualKeys_Integrations_IntegrationId",
                table: "VirtualKeys");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropIndex(
                name: "IX_VirtualKeys_IntegrationId",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "IntegrationRole",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "IntegrationScopes",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "MaxOutputTokens",
                table: "ModelDeployments");
        }
    }
}
