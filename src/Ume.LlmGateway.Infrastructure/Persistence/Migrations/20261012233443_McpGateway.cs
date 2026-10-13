using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "McpCallsPerMinute",
                table: "VirtualKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "McpMethod",
                table: "UsageRecords",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "McpServerId",
                table: "UsageRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "McpTool",
                table: "UsageRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentRequestId",
                table: "UsageRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "McpServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Residency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerDepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EncryptedCredential = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ForwardHeaders = table.Column<List<string>>(type: "text[]", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxResultBytes = table.Column<int>(type: "integer", nullable: false),
                    AutoApproveNewTools = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Capabilities = table.Column<int>(type: "integer", nullable: false),
                    Instructions = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpTools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false),
                    ApprovalState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ApprovedDefinition = table.Column<string>(type: "jsonb", nullable: true),
                    ExposedName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PriceSek = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpTools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpTools_McpServers_McpServerId",
                        column: x => x.McpServerId,
                        principalTable: "McpServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VirtualKeyMcpGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VirtualKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedTools = table.Column<List<string>>(type: "text[]", nullable: false),
                    ApprovalPolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AllowResources = table.Column<bool>(type: "boolean", nullable: false),
                    AllowPrompts = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VirtualKeyMcpGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VirtualKeyMcpGrants_McpServers_McpServerId",
                        column: x => x.McpServerId,
                        principalTable: "McpServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VirtualKeyMcpGrants_VirtualKeys_VirtualKeyId",
                        column: x => x.VirtualKeyId,
                        principalTable: "VirtualKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_McpServerId_Timestamp",
                table: "UsageRecords",
                columns: new[] { "McpServerId", "Timestamp" },
                filter: "\"McpServerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpServers_Slug",
                table: "McpServers",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpTools_ExposedName",
                table: "McpTools",
                column: "ExposedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpTools_McpServerId_Name",
                table: "McpTools",
                columns: new[] { "McpServerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VirtualKeyMcpGrants_McpServerId",
                table: "VirtualKeyMcpGrants",
                column: "McpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_VirtualKeyMcpGrants_VirtualKeyId_McpServerId",
                table: "VirtualKeyMcpGrants",
                columns: new[] { "VirtualKeyId", "McpServerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpTools");

            migrationBuilder.DropTable(
                name: "VirtualKeyMcpGrants");

            migrationBuilder.DropTable(
                name: "McpServers");

            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_McpServerId_Timestamp",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "McpCallsPerMinute",
                table: "VirtualKeys");

            migrationBuilder.DropColumn(
                name: "McpMethod",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "McpServerId",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "McpTool",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ParentRequestId",
                table: "UsageRecords");
        }
    }
}
