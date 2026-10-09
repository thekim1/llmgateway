using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthFailures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FirstSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    VirtualKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    KeyPrefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthFailures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthFailures_LastSeen",
                table: "AuthFailures",
                column: "LastSeen");

            migrationBuilder.CreateIndex(
                name: "IX_AuthFailures_VirtualKeyId_LastSeen",
                table: "AuthFailures",
                columns: new[] { "VirtualKeyId", "LastSeen" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthFailures");
        }
    }
}
