using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedRulesDataKeysPii : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data API keys and the PII incidents index.
            migrationBuilder.CreateTable(
                name: "DataApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataApiKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_Timestamp_Pii",
                table: "UsageRecords",
                column: "Timestamp",
                filter: "\"PiiCategories\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DataApiKeys_KeyHash",
                table: "DataApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataApiKeys_Name",
                table: "DataApiKeys",
                column: "Name",
                unique: true);

            // A rule can now be shared by several keys, teams or departments: ScopeId becomes the set ScopeIds.
            migrationBuilder.AddColumn<List<Guid>>(
                name: "ScopeIds",
                table: "RoutingRules",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.Sql("""UPDATE "RoutingRules" SET "ScopeIds" = ARRAY["ScopeId"] WHERE "ScopeId" IS NOT NULL;""");

            migrationBuilder.DropIndex(
                name: "IX_RoutingRules_Scope_ScopeId_Priority",
                table: "RoutingRules");

            migrationBuilder.DropColumn(
                name: "ScopeId",
                table: "RoutingRules");

            migrationBuilder.AddColumn<bool>(
                name: "IsTemplate",
                table: "RoutingRules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_Scope_Priority",
                table: "RoutingRules",
                columns: new[] { "Scope", "Priority" });

            // Example rules, disabled, seeded once with fixed ids (a deleted template stays deleted). Skipped when a global
            // rule with the same name already exists.
            migrationBuilder.Sql(TemplateSql);
        }

        internal const string TemplateSql = """
            INSERT INTO "RoutingRules" ("Id", "Name", "Description", "IsEnabled", "IsTemplate", "Priority", "Scope", "ScopeIds", "Condition", "Chain", "Fallbacks", "CreatedAt", "UpdatedAt")
            SELECT v."Id", v."Name", v."Description", false, true, v."Priority", 'Global', '{}'::uuid[], v."Condition", false, '{}'::text[], now(), now()
            FROM (VALUES
                ('0199d2a0-7e00-7000-8000-000000000001'::uuid, 'Personal data to a local model',
                 'Template. Chats in which the gateway finds personal data (personnummer, email address, phone number, IBAN) go to an on-prem model. Create a route alias named ''local'' for your on-prem model (or change the target), then switch the rule on. Keep it first in the order and without fallbacks, so personal data never falls back to an external provider.',
                 -100, 'pii_detected && endpoint == "chat_completions"'),
                ('0199d2a0-7e00-7000-8000-000000000002'::uuid, 'Opt in to the local model with a header',
                 'Template. A client sends the header x-ume-route: local to have its request served by the on-prem model. Create a route alias named ''local'' (or change the target) before switching it on.',
                 900, 'headers["x-ume-route"] == "local"'),
                ('0199d2a0-7e00-7000-8000-000000000003'::uuid, 'Large prompts to a long-context model',
                 'Template. Requests with an estimated prompt of more than 30,000 tokens go to a model with a large context window. Create a route alias named ''long-context'' (or change the target) before switching it on.',
                 910, 'prompt_tokens > 30000'),
                ('0199d2a0-7e00-7000-8000-000000000004'::uuid, 'Budget nearly used: cheaper model',
                 'Template. When more than 90 % of the key''s, team''s or department''s budget is used, requests go to a cheaper model. Create a route alias named ''cheap'' (or change the target) before switching it on.',
                 920, 'budget_used > 90')
            ) AS v("Id", "Name", "Description", "Priority", "Condition")
            WHERE NOT EXISTS (SELECT 1 FROM "RoutingRules" r WHERE r."Scope" = 'Global' AND lower(r."Name") = lower(v."Name"))
            ON CONFLICT ("Id") DO NOTHING;

            INSERT INTO "RoutingRuleTargets" ("Id", "RoutingRuleId", "Model", "Weight")
            SELECT v."Id", v."RuleId", v."Model", 1
            FROM (VALUES
                ('0199d2a0-7e00-7000-8000-000000000101'::uuid, '0199d2a0-7e00-7000-8000-000000000001'::uuid, 'local'),
                ('0199d2a0-7e00-7000-8000-000000000102'::uuid, '0199d2a0-7e00-7000-8000-000000000002'::uuid, 'local'),
                ('0199d2a0-7e00-7000-8000-000000000103'::uuid, '0199d2a0-7e00-7000-8000-000000000003'::uuid, 'long-context'),
                ('0199d2a0-7e00-7000-8000-000000000104'::uuid, '0199d2a0-7e00-7000-8000-000000000004'::uuid, 'cheap')
            ) AS v("Id", "RuleId", "Model")
            WHERE EXISTS (SELECT 1 FROM "RoutingRules" r WHERE r."Id" = v."RuleId")
            ON CONFLICT ("Id") DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Untouched templates go; a rule shared by several owners keeps only its first owner.
            migrationBuilder.Sql("""DELETE FROM "RoutingRules" WHERE "IsTemplate";""");

            migrationBuilder.DropIndex(
                name: "IX_RoutingRules_Scope_Priority",
                table: "RoutingRules");

            migrationBuilder.DropColumn(
                name: "IsTemplate",
                table: "RoutingRules");

            migrationBuilder.AddColumn<Guid>(
                name: "ScopeId",
                table: "RoutingRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""UPDATE "RoutingRules" SET "ScopeId" = "ScopeIds"[1] WHERE cardinality("ScopeIds") > 0;""");

            migrationBuilder.DropColumn(
                name: "ScopeIds",
                table: "RoutingRules");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_Scope_ScopeId_Priority",
                table: "RoutingRules",
                columns: new[] { "Scope", "ScopeId", "Priority" });

            migrationBuilder.DropTable(
                name: "DataApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_Timestamp_Pii",
                table: "UsageRecords");
        }
    }
}
