using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Removes what the reverted migration <c>20261012233443_McpGateway</c> (0.4.0) created. Databases that never ran it
    /// are left as they are, and its history row is deleted so a later migration may reuse the schema. Rows recorded for
    /// the removed endpoints <c>Mcp</c> and <c>McpToolCall</c> are deleted too: <c>GatewayEndpoint</c> no longer has these
    /// values, so EF Core could not read them back (the usage and PII incident lists would fail).
    /// </summary>
    public partial class RemoveMcpGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "UsageRecords" WHERE "Endpoint" IN ('Mcp', 'McpToolCall');
                DELETE FROM "UsageMonthlyTotals" WHERE "Endpoint" IN ('Mcp', 'McpToolCall');
                DELETE FROM "AuthFailures" WHERE "Endpoint" IN ('Mcp', 'McpToolCall');
                DROP TABLE IF EXISTS "VirtualKeyMcpGrants";
                DROP TABLE IF EXISTS "McpTools";
                DROP TABLE IF EXISTS "McpServers";
                DROP INDEX IF EXISTS "IX_UsageRecords_McpServerId_Timestamp";
                ALTER TABLE "UsageRecords"
                    DROP COLUMN IF EXISTS "McpServerId",
                    DROP COLUMN IF EXISTS "McpMethod",
                    DROP COLUMN IF EXISTS "McpTool",
                    DROP COLUMN IF EXISTS "ParentRequestId";
                ALTER TABLE "VirtualKeys" DROP COLUMN IF EXISTS "McpCallsPerMinute";
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012233443_McpGateway';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The MCP schema is not restored: the code that used it was removed in 0.4.1.
        }
    }
}
