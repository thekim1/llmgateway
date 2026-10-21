using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditActorContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorDepartments",
                table: "AuditLog",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorName",
                table: "AuditLog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorRoles",
                table: "AuditLog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "AuditLog",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "AuditLog",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAddress",
                table: "AuditLog",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "AuditLog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActorDepartments",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ActorName",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ActorRoles",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "SourceAddress",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "AuditLog");
        }
    }
}
