GRANT SELECT ON ALL TABLES IN SCHEMA public TO ume_gateway, ume_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ume_gateway, ume_admin;
GRANT INSERT ON "UsageRecords", "AlertEvents", "AuthFailures", "DataProtectionKeys" TO ume_gateway;
GRANT UPDATE ("LastUsedAt") ON "VirtualKeys" TO ume_gateway;
GRANT INSERT, UPDATE, DELETE ON "Departments", "Teams", "VirtualKeys", "ProviderAccounts",
  "ModelDeployments", "ModelPrices", "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets",
  "Budgets", "ExchangeRates" TO ume_admin;
GRANT INSERT ON "AuditLog", "DataProtectionKeys" TO ume_admin;
GRANT UPDATE ("Acknowledged") ON "AlertEvents" TO ume_admin;
