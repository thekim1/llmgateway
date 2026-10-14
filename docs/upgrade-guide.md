# Upgrade guide and release checklist

## POC 0.1.0

Initial demonstrator: .NET 10.0.12, Aspire 13.6, Vue 3, PostgreSQL 17 and Redis 8.6.
Production deployment now uses chiseled-extra images, immutable base digests, file-mounted
secrets, verified TLS, restricted DB roles/Redis ACL, static SPA serving and segmented Compose.
See [runbook](runbook.md) for the actual deployment procedure.

## Next release: shared routing rules

- The migration `RoutingRuleScopeSets` converts `RoutingRules.ScopeId` into the set `ScopeIds` (`uuid[]`), drops
  `ScopeId`, adds `IsTemplate` and inserts four disabled template rules. It is not expand/contract compatible: gateways and
  the admin API of the previous version cannot read the new schema, so drain traffic, migrate and roll out both together.
- Admin API clients: send `scopeIds: [...]` instead of `scopeId` when creating, updating or reassigning a rule, and
  `{ scope, ruleIds }` to reorder. Responses carry `scopeIds` and `owners` instead of `scopeId`/`scopeName`.

## Next release: data retention

- **Data older than 365 days is removed the first night after the upgrade** (usage records, refused-key records, ended
  budget alerts, audit entries), with monthly usage totals kept. If your records-retention decision says otherwise, set
  *Settings > Data retention* right after deploying, or deploy with `Retention__Enabled=false` on the admin API until it is set.
- The migration `DataRetention` adds retention columns to `GatewaySettings`, the table `UsageMonthlyTotals` and the function
  `ume_retention_step`; it is additive. Re-run the role grants (the migration service does it) so `ume_admin` may execute the
  function: until then runs fail with `SQL state 42501`, shown on the Settings page.

## Next release: client address restrictions

- The migration `KeyIpRangesTrustedProxies` adds `VirtualKeys.AllowedIpRanges` (`text[]`, default empty) and the table
  `GatewaySettings`. It is additive: the previous gateway and admin API keep working against the new schema. `app-roles.sql`
  grants the admin role writes on `GatewaySettings` and the Data API role `SELECT` on the new column; the migration service
  applies it on start. Run the migration before starting the new Data API, whose key inventory reads the column.
- `UME_FORWARDED_HEADERS` / `ASPNETCORE_FORWARDEDHEADERS_ENABLED` no longer affect the gateway. Behind a reverse proxy, list
  the proxy under *Settings > Trusted proxies* after the upgrade, or refused keys are recorded with the proxy's address and
  keys given an address restriction are refused. Check the proxy sends `X-Forwarded-For` (see the runbook).
- New 403 error code `address_not_allowed` for clients of keys with an address restriction.

## Next release: usage dashboard

- The migration `RequestTimings` adds the nullable columns `UsageRecords.UpstreamMs` and `TimeToFirstTokenMs` (no table
  rewrite). It is additive: the previous gateway keeps writing records without them. The existing grants cover the new
  columns (the gateway inserts and the Data API reads the whole table).
- Records written before the upgrade have no provider time or time to first token, so the *Gateway overhead* and
  *Time to first token* cards fill in from the upgrade on.
- The Data API usage feed and `GET /api/usage/requests` have the new fields `upstreamMs` and `timeToFirstTokenMs`
  (`/api/usage/requests` also `costUsd`); clients that ignore unknown fields need no change.

## 0.4.1: MCP gateway removed

- The migration `RemoveMcpGateway` drops the MCP tables and columns of 0.4.0 (migration `McpGateway`) and deletes its
  history row. It is not expand/contract compatible with 0.4.0: a 0.4.0 gateway or admin API fails on the dropped columns,
  so stop them, migrate and start 0.4.1 together. On a database that never ran 0.4.0 it changes nothing.
- It also **deletes the rows of MCP traffic**: usage records, monthly totals and refused-key records with `Endpoint` `Mcp` or
  `McpToolCall`. 0.4.1 cannot read them, so they would break *Usage > Requests and costs* and *PII incidents*. Their cost no
  longer counts in usage reports or in budget counters rebuilt from Postgres. If you need the MCP calls for accounting,
  export them before upgrading: `SELECT * FROM "UsageRecords" WHERE "Endpoint" IN ('Mcp', 'McpToolCall')`.
- LLM requests that ran inside an MCP tool loop (`/v1/responses` turns) are ordinary records and are kept; only their
  `ParentRequestId` link is dropped.
- Redeploy the stack or run `init-deployment.sh` and restart Redis so the ACL no longer allows `ume:mcp:events`.

## Upgrade procedure

1. Review release/source changes, package/image advisories, licenses and SBOM; approve exact
   application/base digests. Keep Aspire SDK/hosting packages on the 13.6 release family.
2. Stop the exact AppHost before local builds. Run all test suites, production deployment
   checks and accessibility/manual regression appropriate to the change.
3. Back up the database, pepper, Data Protection key ring/certificate and nonsecret config.
   Rehearse restoring the backup in isolation; record RPO/RTO.
4. Review migrations for expand/contract compatibility. Drain gateway traffic; run the
   one-shot migration/role-grant job with **only** migrator credentials. Do not give DDL to apps.
5. Deploy digest-pinned API images; verify health, schema/version, queue drain, OIDC/CSRF,
   role scoping and a synthetic inference request. Preserve credentials/key encryption material.
6. Observe error/fallback rates and spend reconciliation. Record release, digests, schema,
   test evidence and approval in change management.

Routing rules add the migrations `RoutingRules` (tables `RoutingRules`, `RoutingRuleTargets`) and
`RoutingRuleOnUsage` (two nullable columns on `UsageRecords`). Both are additive, so the previous gateway image keeps
working against the new schema. `deploy/postgres/app-roles.sql` now also grants the admin role write access to the
new tables; re-run the role-grant job with the migration.

Security events add the migrations `AuthFailures` (new table) and `FeedRecordedAt` (a `RecordedAt` column with a
database default on `UsageRecords`, `AuthFailures` and `AuditLog`; existing rows get the migration time). Both are
additive. `app-roles.sql` grants the gateway role `INSERT` on `AuthFailures`; re-run the role-grant job.

Speech to text and live audio add the migration `AudioTranscription`: `AudioSeconds` on `UsageRecords`, and
`AudioPerMinuteUsd`, `AudioInputPerMillionUsd` and `AudioOutputPerMillionUsd` on `ModelPrices`, all `NOT NULL DEFAULT 0`,
so it is additive and needs no new grants. After deploying, give the providers that serve speech models the *Speech to
text* capability, add the models with kind *Speech to text* (or rediscover them) and their per-minute or per-token
price, and create an alias such as `ume/transcribe`. If a proxy sits in front of the gateway, allow at least 32 MB
bodies and turn request buffering off (see the runbook).

Image generation (`/v1/images/generations`, `/v1/images/edits`) adds the migration `ImageGeneration`: `PerImageUsd` on
`ModelPrices`, `NOT NULL DEFAULT 0`, so it is additive and needs no new grants. After deploying, give the providers that serve
image models the *Image generation and editing* capability, add the models with kind *Image generation* (or rediscover them)
and their per-picture or per-token price, and create an alias such as `ume/image`. Image edits upload up to 50 MB: allow at
least 52 MB bodies at the proxy (`Gateway__MaxImageRequestBodyBytes`; raise both together).

Live audio (`/v1/realtime`, `/v1/realtime/translations`) needs three more steps:
- **Redis ACL.** The per-key session limit uses a sorted set: add `+zadd +zrem +zcard +zremrangebyscore` to the
  `gateway` user in `secrets/redis/users.acl` (the template `deploy/redis/users.acl.example` has them). Since 0.3.1 the
  stack's bootstrap and `init-deployment.sh` rewrite `users.acl` from the template on every start, so a redeploy (or
  running `init-deployment.sh` again) followed by a Redis restart is enough; before, existing installations kept their
  generated file and had to edit it by hand. Until then sessions work, but the
  limit is not enforced and the gateway logs a warning per session.
- **Proxy.** Pass the WebSocket upgrade: the `map $http_upgrade $ume_connection` block and the `Upgrade` /
  `Connection` headers in `deploy/proxy/nginx.conf.example`. Without them, clients get 400 (not a WebSocket request).
- **Providers.** Give the providers the *Live audio (realtime)* capability and add the models: speech-to-text models
  for live transcription, *Realtime (live)* models, *Live interpreting* models (`gpt-realtime-translate`), with their
  audio token or per-minute prices. Realtime models price audio tokens far above text tokens, so set the audio token
  prices or their cost is under-reported.

The control- and data-plane cleanup needs no migration, but check these before upgrading:
- **Startup validation.** The admin API refuses to start without `Oidc:Authority`, with `Admin:RequestsPerMinute`
  outside 1–10000, or with a `Gateway:BaseUrl`/`Gateway:OperationsUrl` that is not an absolute http(s) URL; the
  gateway validates `Gateway:FallbackSekPerUsd` (0.01–1000) and the migration service its `Seed:*` settings.
- **Exchange rate.** If no USD exchange rate has been entered, the gateway's readiness check `exchange-rate` now
  reports Degraded and each catalogue load logs a warning (costs still use `Gateway:FallbackSekPerUsd`, 10). Enter
  the rate under Settings.
- **Audit details.** New entries hold dedicated snapshots (camelCase, enum names, no derived fields); older entries
  keep their shape, so a consumer of `details` (Data API `security.read`, SIEM) must accept both.
- **CSV.** The admin usage export quotes cells only when needed (it quoted every text cell) and no longer prefixes
  numeric text such as `-1` with `'`; the Data API now also prefixes formulas after leading spaces or starting with a
  line feed.
- **Alert webhooks** are sent once, without retries, from their own queue.

### Adding the Data API to an existing installation

New installations get the read-only database role `ume_data` from `init.sql`. An existing database was initialised
before that, and creating a role needs the database superuser, so it is one manual step. The Data API is optional;
skip this until you want it.

1. Re-run `deploy/init-deployment.sh` (Portainer: redeploy the stack, which runs the bootstrap). Existing secrets are
   kept; it adds `secrets/postgres/data_password`, `secrets/dataapi/` and `certs/dataapi/`.
2. Create the role (idempotent; reads the password file inside the Postgres container and reloads `pg_hba.conf`):

   ```bash
   docker compose -f deploy/compose.prod.yaml --env-file deploy/.env exec -T postgres \
     sh -c 'PGPASSWORD=$(cat /run/postgres-secrets/bootstrap_password) psql -q -v ON_ERROR_STOP=1 -U postgres -d gatewaydb' \
     < deploy/postgres/add-data-role.sql
   ```

   The Postgres container must be running with the new `pg_hba.conf` (it lists `ume_data`); `docker compose up -d`
   with the updated files does that.
3. Re-run the migrations job so `app-roles.sql` grants the role its read access (it skips the grants while the role does
   not exist), then start the Data API: set `COMPOSE_PROFILES=data` in `deploy/.env` and run `docker compose ... up -d
   --wait`. If the Data API was started before the role existed, give its health check a few seconds to recover.

Schema-breaking changes require an explicit downtime/rollback plan. Image rollback is safe
only if the previous app supports the new schema. Do not automatically run destructive
down-migrations; restore an approved backup in isolation first. Redis counters reconstructed
after restart depend on committed usage and can lag in-flight requests.

## Certificate and secret rotation

Renew TLS certificates with appropriate SANs/CA, stage trust changes, restart the exact
services, verify strict validation. Rotate Redis ACL/client passwords together. Update
existing Postgres role passwords through an approved DBA workflow as well as secret files.
Keep old Data Protection decrypting certificates available during re-encryption/key-ring
transition; replacing the encryption certificate blindly loses provider credentials/cookies.
HMAC pepper rotation needs a designed key-hash migration or explicit replacement/revocation
of all virtual keys; the POC does not support transparent dual-pepper rotation.

## Dependency and supply-chain commands

```powershell
dotnet list UmeLlmGateway.slnx package --vulnerable --include-transitive
Set-Location src\admin-ui
npm audit --audit-level=low
Set-Location ..\..\tests\e2e
npm audit --audit-level=low
```

Generate an SPDX SBOM with the approved Microsoft `sbom-tool` version against each publish
directory; review package/image scans and provenance before promoting images. SBOM inputs
must exclude secret/certificate mounts, test results and developer environments. Dependency
audit success is time-bound, not proof of absence of vulnerabilities; container OS/package
scanning and signed artifact attestations remain municipal release responsibilities.
