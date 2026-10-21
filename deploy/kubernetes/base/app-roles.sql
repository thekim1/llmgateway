-- Applied by the migration service on every start. The service roles' table privileges are revoked and granted again in
-- one transaction, so each role holds exactly what this file lists (a privilege removed here is removed from existing
-- installations too) and running services see either the old or the new grants, never none. Tables are named one by
-- one: a new table is readable by no service until it is added here (DatabaseRoleTests checks the effective grants).
-- No curly braces: the migration service runs this file with EF Core's ExecuteSqlRaw, which reads them as placeholders.
BEGIN;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM ume_gateway, ume_admin;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM ume_gateway, ume_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ume_gateway, ume_admin;

-- The settings row exists before any service runs, so the admin role needs no INSERT on it (an insert could set
-- UsageRetainedFrom, which only the retention function may move).
INSERT INTO "GatewaySettings" ("Id", "TrustedProxies", "AuditRetentionDays") VALUES (1, ARRAY[]::text[], 365) ON CONFLICT ("Id") DO NOTHING;

-- Gateway (faces the clients): what it authenticates, routes and accounts with. Not the audit log or Data API keys. It reads
-- the Data Protection key ring but does not add keys (it only decrypts; the admin API creates and rotates them).
GRANT SELECT ON "Departments", "Teams", "VirtualKeys", "ProviderAccounts", "ModelDeployments", "ModelPrices",
  "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets", "Budgets", "ExchangeRates", "GatewaySettings",
  "UsageRecords", "UsageMonthlyTotals", "AlertEvents", "AuthFailures", "DataProtectionKeys" TO ume_gateway;
GRANT INSERT ON "UsageRecords", "AlertEvents", "AuthFailures" TO ume_gateway;
GRANT UPDATE ("LastUsedAt") ON "VirtualKeys" TO ume_gateway;

-- Admin API: reads everything it shows (and the schema version for Operations); changes configuration, appends audit
-- entries, creates Data Protection keys. Settings column by column, without UsageRetainedFrom.
GRANT SELECT ON "Departments", "Teams", "VirtualKeys", "DataApiKeys", "ProviderAccounts", "ModelDeployments", "ModelPrices",
  "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets", "Budgets", "ExchangeRates", "GatewaySettings",
  "UsageRecords", "UsageMonthlyTotals", "AuditLog", "AlertEvents", "AuthFailures", "DataProtectionKeys",
  "__EFMigrationsHistory" TO ume_admin;
GRANT INSERT, UPDATE, DELETE ON "Departments", "Teams", "VirtualKeys", "ProviderAccounts",
  "ModelDeployments", "ModelPrices", "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets",
  "Budgets", "ExchangeRates", "DataApiKeys" TO ume_admin;
GRANT UPDATE ("TrustedProxies", "LogAllRequests", "LogAllRequestsUntil", "UpdatedAt", "UsageRetentionDays", "AuditRetentionDays",
  "RetentionLastRunAt", "RetentionLastRunDeleted", "RetentionLastRunError") ON "GatewaySettings" TO ume_admin;
GRANT INSERT ON "AuditLog", "DataProtectionKeys" TO ume_admin;
GRANT UPDATE ("Acknowledged") ON "AlertEvents" TO ume_admin;
-- Data retention: old rows are removed only through this function (owner's rights, minimum 90 days); the admin role
-- has no DELETE on usage, refused-credential, alert or audit rows.
GRANT EXECUTE ON FUNCTION ume_retention_step(integer) TO ume_admin;
-- Data API (optional): read-only, and column by column where a table holds secrets, so the role cannot
-- read key hashes, encrypted key secrets or provider credentials even through a bug. Skipped while the
-- role does not exist (it is created by init.sql on new installs; see docs/upgrade-guide.md for existing ones).
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_data') THEN
    GRANT CONNECT ON DATABASE gatewaydb TO ume_data;
    GRANT USAGE ON SCHEMA public TO ume_data;
    GRANT SELECT ON "UsageRecords", "AuthFailures", "AuditLog", "Departments", "Teams", "ModelDeployments",
      "ModelPrices", "Budgets", "ExchangeRates", "UsageMonthlyTotals" TO ume_data;
    GRANT SELECT ("UsageRetainedFrom") ON "GatewaySettings" TO ume_data;
    GRANT SELECT ("Id", "TeamId", "Name", "Description", "Prefix", "IsEnabled", "CreatedAt", "CreatedBy", "ExpiresAt",
      "RevokedAt", "GraceUntil", "RotatedToKeyId", "LastUsedAt", "AllowedModels", "AllowedResidencies", "AllowedProviders",
      "AllowedIpRanges", "PiiPolicy", "AttachmentPolicy", "RequestsPerMinute", "TokensPerMinute", "AllowProviderTools",
      "AllowProviderStorage") ON "VirtualKeys" TO ume_data;
    GRANT SELECT ("Id", "Name", "DisplayName", "Type", "Residency", "IsEnabled") ON "ProviderAccounts" TO ume_data;
    -- Its own credentials: the key hash (SHA-256 of a 256-bit random key) to authenticate Data API keys, and the
    -- only column it may write, the approximate last use.
    GRANT SELECT ("Id", "Name", "Prefix", "KeyHash", "Permissions", "ExpiresAt", "RevokedAt", "LastUsedAt") ON "DataApiKeys" TO ume_data;
    GRANT UPDATE ("LastUsedAt") ON "DataApiKeys" TO ume_data;
  END IF;
END $$;
COMMIT;
