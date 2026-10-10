GRANT SELECT ON ALL TABLES IN SCHEMA public TO ume_gateway, ume_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ume_gateway, ume_admin;
GRANT INSERT ON "UsageRecords", "AlertEvents", "AuthFailures", "DataProtectionKeys" TO ume_gateway;
GRANT UPDATE ("LastUsedAt") ON "VirtualKeys" TO ume_gateway;
GRANT INSERT, UPDATE, DELETE ON "Departments", "Teams", "VirtualKeys", "ProviderAccounts",
  "ModelDeployments", "ModelPrices", "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets",
  "Budgets", "ExchangeRates" TO ume_admin;
GRANT INSERT ON "AuditLog", "DataProtectionKeys" TO ume_admin;
GRANT UPDATE ("Acknowledged") ON "AlertEvents" TO ume_admin;
-- Data API (optional): read-only, and column by column where a table holds secrets, so the role cannot
-- read key hashes, encrypted key secrets or provider credentials even through a bug. Skipped while the
-- role does not exist (it is created by init.sql on new installs; see docs/upgrade-guide.md for existing ones).
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_data') THEN
    GRANT CONNECT ON DATABASE gatewaydb TO ume_data;
    GRANT USAGE ON SCHEMA public TO ume_data;
    GRANT SELECT ON "UsageRecords", "AuthFailures", "AuditLog", "Departments", "Teams", "ModelDeployments",
      "ModelPrices", "Budgets", "ExchangeRates" TO ume_data;
    GRANT SELECT ("Id", "TeamId", "Name", "Description", "Prefix", "IsEnabled", "CreatedAt", "CreatedBy", "ExpiresAt",
      "RevokedAt", "GraceUntil", "RotatedToKeyId", "LastUsedAt", "AllowedModels", "AllowedResidencies", "AllowedProviders",
      "PiiPolicy", "AttachmentPolicy", "RequestsPerMinute", "TokensPerMinute") ON "VirtualKeys" TO ume_data;
    GRANT SELECT ("Id", "Name", "DisplayName", "Type", "Residency", "IsEnabled") ON "ProviderAccounts" TO ume_data;
  END IF;
END $$;
