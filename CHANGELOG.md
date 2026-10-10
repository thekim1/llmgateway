# Changelog

## Unreleased

Data API (phase 2 of `docs/data-access.md`): new optional service `Ume.LlmGateway.DataApi` with read-only, versioned endpoints for BI, management and security integrations. **Access:** OAuth 2.0 client-credentials tokens from the organisation's IdP (`DataApi:Authority`, audience `ume-data-api`). Permissions `usage.aggregate`, `usage.detail`, `security.read` and `catalog.read` are read from `scope`, `scp` or `roles` (Keycloak, AD FS, Entra ID). There is a per-client rate limit, and every read is logged as the security event `data.read`. **Endpoints:** `/v1/usage/records`, `/v1/usage/aggregate` (day or month, in `DataApi:TimeZone`), `/v1/security/auth-failures`, `/requests`, `/audit` and `/keys`, and `/v1/catalog/*`; JSON, CSV or NDJSON (`?format=`). **Feeds:** cursor feeds hand out only rows older than `DataApi:SettleSeconds` (60) and stop at the first unsettled row, so rows committed out of id order by concurrent writers are never skipped. Migration `FeedRecordedAt` adds a database-set `RecordedAt` column to `UsageRecords`, `AuthFailures` and `AuditLog`. **Privacy:** aggregates fold departments with fewer than `DataApi:MinimumGroupSize` (5) keys per period, and `usage.detail` omits PII categories unless `DataApi:Detail:IncludePiiCategories`. **Database:** it connects as a new role `ume_data` (`init.sql` on new installs; `deploy/postgres/add-data-role.sql` for existing ones, see the upgrade guide). The role has `SELECT` only, granted column by column on `VirtualKeys` and `ProviderAccounts`, so it cannot read key hashes, encrypted secrets or provider credentials; the tests run as this role. **Deployment:** Compose profile `data` in `compose.prod.yaml` and `compose.portainer.yaml` (`DATAAPI_IMAGE`, `UME_DATA_*`). `init-deployment.sh` (`--data-host`) and the Portainer bootstrap create its certificate, password and connection string. The AppHost runs it locally, with a `ume-data-dev` client-credentials client in the dev Keycloak realm, but leaves it out of `aspire publish` unless `DataApi:Enabled=true`. Verified end to end with a real Keycloak token, and with `compose.prod.yaml` plus the `data` profile, including the upgrade procedure for an existing database.

Unknown virtual keys are cached (`KeyAuthenticator`): a well-formed key that is not in the database is remembered for `Gateway:KeyCacheSeconds` in a separate cache capped at 10 000 entries, so made-up keys cannot evict real ones. A client retrying with a deleted or mistyped key no longer costs one Postgres query per request. In the `audit` benchmark, which gains an "unknown key (401)" scenario, a refused request goes from 0.99 ms with 1 query to 0.12 ms with none. Creating, rotating or re-enabling a key publishes a key invalidation, which clears the cache, so new keys work immediately. **Fix:** refused keys on `GET /v1/models` were recorded as `ChatCompletions`; they now use the new `GatewayEndpoint.Models` (no migration: the column is a string).

No manual screen reader testing is planned (no resources); `HANDOVER.md` and the accessibility statement say so, and the statement still does not claim a screen reader pass.

Security events and recorded refused keys (phase 1 of `docs/data-access.md`, the design for giving BI, management and security teams the gateway's data; ADR 015). The gateway and admin API log security events under the category `Ume.LlmGateway.Security` with stable names and fields: `gateway.auth.failed`, `gateway.pii.action`, `gateway.request.refused` (`attachment_not_allowed`, `model_not_allowed`) and `admin.change` (every saved audit entry, without its details). Missing, unknown, revoked, expired and disabled keys, and keys whose team or department is inactive, are counted in memory per reason, endpoint, key and client address. They are written to the new `AuthFailures` table and logged every `Gateway:Security:AuthFailureFlushSeconds` (10). A cap of `MaxAuthFailureBuckets` (1 000) rows per interval keeps unauthenticated traffic from growing the table, and the presented key is never stored. The client address is truncated to /24 (IPv4) or /48 (IPv6) by default (`Gateway:Security:SourceAddress`: `Truncated`, `Full`, `None`). Migration `AuthFailures`; `app-roles.sql` grants the gateway role `INSERT` on it. New `deploy/otel/collector-siem.example.yaml` routes the security category to syslog, Splunk, Elastic or Sentinel. It was validated with collector-contrib 0.120.0, which receives the category as the instrumentation scope. The Compose files gain `UME_OTEL_ENDPOINT`, `UME_SOURCE_ADDRESS` and `UME_FORWARDED_HEADERS`. See the README, *Security events and data for other teams*, and the runbook, *Security events and SIEM*.

Per-key policy for attached files: `VirtualKey.AttachmentPolicy` (`Allowed` default, `ImagesOnly`, `None`), set in the admin UI (key form, *Attached files*) and via `attachmentPolicy` on `POST`/`PUT /api/keys` (omitted = unchanged on update; copied on rotation). The gateway finds image, document and audio parts in Chat Completions, Responses and Anthropic Messages bodies (including parts nested in tool results) and rejects refused ones with 400 `attachment_not_allowed` before rate limiting. Migration `KeyAttachmentPolicy` sets existing keys to `Allowed`. **Fix:** the OpenAI → Anthropic translator silently dropped `file` and `input_audio` parts (and any unknown part type); PDF and plain-text files are now sent as `document` blocks, and parts Claude cannot take return 400 `unsupported_content`. See the README, *Attached files*.

Data protection and resilience comparison with eneo (PII scanning, model restrictions, provider fallback, eneo running behind the gateway; `ENEO_LLM_ENDPOINT`/`ENEO_LLM_MODEL`/`ENEO_LLM_API_KEY` in the compare stack), in `docs/performance.md` and three new report sections. Management report `docs/reports/platform-comparison.html` (offline, animated, keyboard-stepped; prints one section per A4 page) and `.pdf`, and the performance work list `docs/performance-improvement-plan.md`. Platform comparison with eneo v2.2.1: `benchmarks/compare` (Docker Compose, k6, both platforms against the same fake LLM with pinned CPUs; reports latency, throughput, time to first byte, CPU, Postgres statements and Redis commands per message). `compare-seed` mode in the benchmark project seeds a standalone gateway database. Results in `docs/performance.md`, *Comparison with eneo*. `benchmarks/compare/scale.sh` adds load scaling (1–256 clients) and resource scaling (1–6 vCPUs, peak per size) with CPU sets aligned to physical cores; findings in *Scaling* (eneo: database pool starvation under load; gateway: usage writer caps one instance at about 11 000 msg/s).

Gateway performance (see `docs/performance.md`). New `benchmarks/Ume.LlmGateway.Benchmarks` (BenchmarkDotNet end-to-end, provider/streaming, request and routing benchmarks, plus an `audit` mode that counts Postgres and Redis calls per request and runs a load test). Changes to the data plane, all covered by new tests in `HotPathTests`:

- Redis round trips per request cut from about 10 sequential to 2 before the first byte and 1 after it: budget reservation is a single Lua script that also reports counters needing seeding (`ISpendLedger.ReserveAsync`); the circuit-state lookup is pipelined with the rate-limit call; reconciliation and token counting are pipelined; the circuit reset on success is fire-and-forget.
- Non-streaming answers are written to the client before accounting runs (with `Content-Length`), and passed through as the provider's bytes instead of being parsed into a JSON tree and re-serialised (large gain for embeddings). `UsageWriter.TrackPending` keeps `FlushAsync` exact.
- Streaming: SSE events are written straight into the response pipe (`SseEvent.WriteTo`), the reader allocates less, and content chunks are scanned for their text length instead of being parsed into a JSON tree (OpenAI-compatible and Anthropic passthrough).
- Key and catalogue caches serve an expired entry while one background query refreshes it, so expiry never blocks requests; concurrent misses share one query. Admin invalidations still apply immediately, and a load that started before an invalidation is no longer cached (this also fixes a narrow race where a just-revoked key could be cached again). The last catalogue snapshot is served if Postgres is down.
- Key-rotation lineage is precomputed per catalogue snapshot (was rebuilt from all rotated keys on every request: about 1 ms and 1.3 MB per request with 10 000 rotations).
- Cold budget counters are seeded once per counter, not once per concurrent request.
- Usage writer: the duplicate check only runs when a batch is retried, and `VirtualKeys.LastUsedAt` is updated at most once per key and minute (was once per batch). **Behaviour change:** `LastUsedAt` has minute resolution.
- PII detection skips the regexes for strings without digits or `@` (same result); request bodies are read into pooled buffers; the request body is no longer cloned for the last (usually only) provider attempt.

Environment-only deployment for Portainer: `deploy/compose.portainer.yaml` (generated by `deploy/build-portainer-stack.sh` from `compose.portainer.template.yaml`, `portainer/bootstrap.sh` and the postgres/redis config files; CI checks it is current). A one-shot `bootstrap` container generates the CA, certificates, passwords, pepper and Redis ACL into Docker volumes, idempotently and with automatic certificate renewal, so no script has to be run on the host. Verified with the real images: all services healthy, secrets stable across a redeploy.

Container health probe (`--health-check`): the URL can be overridden with `UME_HEALTHCHECK_URL`, and a certificate name mismatch on the loopback name is accepted (needed for PKI certificates issued only for the public name); untrusted or expired certificates still fail.

Lint gate: `npm run lint` now fails on warnings like CI does (`lint:fix` added), and `.githooks/pre-commit` runs it for admin UI changes (`git config core.hooksPath .githooks`). Fixed the two `vue/html-quotes` warnings in `GettingStartedView.vue`. The hook calls node directly (works from Visual Studio's git) and skips with a warning when node or `node_modules` is missing. Debug builds of `Ume.LlmGateway.AdminApi` run the admin UI lint (`LintAdminUi` target) and report problems as a warning.

First-run production setup: `deploy/init-deployment.sh` generates the internal CA, service and Data Protection certificates, database/Redis passwords, key pepper and `deploy/.env` (prompting for the OIDC authority and hostnames, or via options); new self-contained, hardened `deploy/compose.prod.yaml` as the recommended production file. Documented in the README and runbook.

Exposure options for `init-deployment.sh`: `--proxy` (Nginx Proxy Manager, nginx, F5; re-encrypting to the HTTPS upstreams, generates `deploy/proxy/nginx.conf` from `nginx.conf.example`) and PKI-issued certificates (`--gateway-cert`, `--admin-cert`, keys, `--ca-file`; `UME_GATEWAY_OPERATIONS_URL` for the admin API's gateway calls). See the runbook, *Reverse proxy and PKI*.

Existing identity providers: the bundled Keycloak is skipped in local runs when `Oidc:Authority` is set (all `Oidc:*` settings are passed to the admin API). The admin API can read roles and department codes from differently named claims (`Oidc:RoleClaim`, `Oidc:DepartmentClaim`), request extra scopes, and derive roles from group membership (`Oidc:GroupClaim`, `Oidc:RoleGroups:<role>`) for Active Directory via Keycloak federation, AD FS or Entra ID. `init-deployment.sh --oidc-client-secret-file` installs a confidential client's secret; `compose.prod.yaml` exposes the mapping settings as `UME_OIDC_*`. Unit tests cover the claim mapping. See the runbook, *Identity provider*.

Routing examples (fallback to another model; personal data stays on-prem) with screenshots in `docs/images`, in the README, `docs/routing-rules.md` and a new *Routing examples* card on the Getting started page. `tests/e2e/docs/screenshots.spec.ts` (`playwright.docs.config.ts`) regenerates the screenshots from the real UI.

Documentation cleanup: the finished implementation plan, routing rules plan and UI verification handover were removed (scope and decisions moved to `docs/architecture.md`, open items to `HANDOVER.md`); `HANDOVER.md` rewritten as a current-state file; the accessibility statement now matches the AA target, three themes and the 34-test e2e suite.

Browser tests (`tests/e2e`) rewritten for the current admin UI (English labels, drawers, `ume-theme`, three themes,
WCAG 2.2 AA with the 24 px target-size rule) and extended with routing rules journeys. New `scripts/Start-E2E.ps1` runs the stack
on a separate, disposable test database. All 34 tests pass (routing rules journeys, UI details, permissions, key rotation, usage signals, audit, focus). The run found four accessibility defects, now fixed: key drawer `dl` markup, rule drawer target list,
dark-theme contrast of destructive buttons (`--danger-fg`), and Overview overflow at 320 px. The dev seeder no longer
crashes on a database without the demo team. An expired session now gives the UI a 401 on `/api` calls instead of a redirect to the identity provider, the audit filter lists `RoutingRule`, the deployment test checks the routing rule role grants, and its SPA smoke test follows the English UI.

Routing rules (see `docs/routing-rules.md`):

- Route resolution extracted from the request handler into `IRouteResolver`; behaviour unchanged.
- Dependency-free condition language (CEL subset) with static validation, safe regex and explain traces.
- Rule evaluation: scopes (key, team, department, global), priority, weighted targets, fallbacks, chaining.
- `RoutingRules` and `RoutingRuleTargets` tables (additive migration `RoutingRules`), loaded and
  compiled once per catalogue snapshot; three example rules in the demo data.
- Read-only usage signals for rules: `BudgetService.PeekUsedPercentAsync` (budget closest to its limit) and
  `IRateLimiter.PeekTokensUsedPercentAsync` (in-memory and Redis), fetched only when a rule uses them.
- **Behaviour change:** the gateway now evaluates routing rules on live requests (after the PII policy, before
  candidate selection). Without rules nothing changes. Adds the `x-ume-rule` response header, `RoutingRuleId` /
  `RoutingRuleName` on usage records (migration `RoutingRuleOnUsage`) and the metric
  `ume.gateway.routing.rule_routed`. Credentials and prompt content are never visible to conditions. Admin API and UI follow.
- Admin API for routing rules (`/api/routing-rules`): CRUD with validation and audit, reorder, reassign, live condition
  validation and a dry run that explains which rule applies and why. Deleting a team or department that has rules now
  requires choosing `routingRules=delete|deactivate` (409 with the list of rules otherwise); deactivated rules are
  *orphaned* until reassigned. Routes and models used by a rule cannot be removed or renamed. A rule scoped to a key
  follows the key through rotation. Global rules are part of config export/import (older documents still import).
- Admin UI: **Routing > Routing rules** page (list by scope in checking order, detail, create/edit with a live-validated condition
  editor, enable/disable, move earlier/later, change owner, delete, **Test a request** dry run). Deleting a team or department that
  has routing rules asks whether to delete the rules or deactivate them until a new owner is assigned. Usage shows the rule that
  routed a request (`routingRuleId`, `routingRuleName` in the usage API). Getting started explains the `x-ume-rule` header.
  Icon font subsets regenerated (`rule`, `arrow_upward`, `help`). 89 new UI tests.
- Docs: ADR 013 (in-house condition language, never widens access) and 014 (ownership, delete-or-deactivate, rotation), architecture section,
  runbook entry for disabling a misbehaving rule.
- Role grants: `deploy/postgres/app-roles.sql` grants the admin role write access to the routing rule tables.

## 0.1.0 - 2026-10-07

Initial POC: OpenAI/Anthropic gateway, HMAC virtual keys and budget-preserving rotation,
hierarchical SEK budgets, rate limits, fallback/residency/PII policies, metadata accounting,
OIDC/BFF scoped administration and Vue 3 four-theme UI.

Aspire 13.6 development stack and generated Compose with mandatory production hardening:
verified TLS, Redis ACL, separate migrator/gateway/admin database roles, nonroot read-only
chiseled-extra containers, network segmentation, static SPA assets and file-mounted secrets.

Automated domain/component/integration/browser/accessibility checks and documentation.
POC delivery is not legal approval, production readiness certification or WCAG certification.
