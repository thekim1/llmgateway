# Data access for BI, management and security

How organisations running the gateway get its data into their own reporting, data warehouse and SIEM, and
how different consumers get different data.

> **Status.** Design accepted 2026-10-10. **Phase 1 (security events and persisted authentication failures) and
> phase 2 (the Data API) are implemented.** Of phase 3, the minimum group size and the PII-category toggle exist;
> retention and purging are still a proposal. Sections say which phase they belong to.

## Goals and constraints

- **The gateway is open source.** Adopters run different databases (often MS SQL), BI tools (Power BI, Qlik,
  Fabric), integration platforms (Azure Data Factory, SSIS, MuleSoft) and SIEMs (Sentinel, Splunk, Elastic, QRadar).
  Nothing here may assume a particular product.
- **No direct database access for consumers.** Large organisations isolate services like this and put an
  integration layer between a service and the people who use its data. The gateway's Postgres schema is an
  implementation detail that changes with every EF migration; consumers get a versioned interface instead.
- **Push for security, pull for analytics.** Security wants events in its SIEM within seconds. BI wants complete,
  repeatable, incremental extracts it can land in its own warehouse.
- **Governance is the adopter's decision.** Retention, minimum group sizes and whether source IP addresses are
  stored differ between organisations and jurisdictions. The gateway makes them **settings with
  privacy-preserving defaults**, documented as decisions each adopter has to make (and record in its DPIA).
- **Metadata only** ([ADR 003](adr.md)). No prompt or response content is ever exposed, by any channel.

## Consumers

| Consumer | Typical questions | Granularity | Channel |
|---|---|---|---|
| Management | Cost and adoption per department, trend, budget status, share of traffic per residency (on-prem / EU / external) | Month × department | Data API, `usage.aggregate` |
| BI / data warehouse | Chargeback per cost centre, model mix, cost per request, cached-token savings, forecasting | Request or day × key × model | Data API, `usage.detail` |
| Security / SOC | Failed and revoked key use, PII blocked or redacted, refused attachments, admin changes, dormant keys, keys without expiry | Event, near real time | Security events (push) and Data API, `security.read` (pull) |
| Department admins | Their own spend and keys | Their department | Existing admin UI and Admin API (unchanged) |

## Architecture

```
                         ┌──────────── adopter's network ─────────────┐
 Gateway ──OTLP logs──►  OTel Collector ──► SIEM (Sentinel, Splunk, Elastic, syslog/CEF)
 Admin API ─OTLP logs─►        │
                               └──► (optional) Kafka / Event Hubs
 Postgres ◄─read-only─ Data API ◄─HTTPS, OAuth client credentials─ ETL (ADF, SSIS, Fabric…) ─► warehouse (MS SQL…) ─► Power BI
```

Three channels, each with one job:

1. **Security events (push, phase 1).** Structured log records in a dedicated category, exported with the
   existing OpenTelemetry pipeline. The adopter's OTel Collector routes them to whatever SIEM it runs. The gateway
   contains no SIEM-specific code.
2. **Data API (pull, phase 2).** A separate, optional, read-only service with a versioned OpenAPI contract,
   incremental cursors and OAuth scopes per consumer class.
3. **Admin API and admin UI (unchanged).** Interactive, scoped to the signed-in user's departments. Not meant for
   integrations.

## Phase 1: security events

### Log category and event names

Every security event is written by the logger category **`Ume.LlmGateway.Security`** (constant
`SecurityEvents.Category`). Filtering on that category is how a collector separates security events from
operational logs. Each record has a stable event name (the `EventId` name, exported by OpenTelemetry as the event
name) and a fixed set of attributes. Attribute names are part of the contract: they are only ever added to, never
renamed or removed, within a major version.

| Event id | Event name | Emitted by | When | Attributes |
|---|---|---|---|---|
| 1001 | `gateway.auth.failed` | Gateway | Once per aggregation bucket (see below) | `Reason`, `Endpoint`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId`, `SourceAddress`, `Count`, `FirstSeen`, `LastSeen` |
| 1002 | `gateway.pii.action` | Gateway | A key's PII policy found personal data (blocked, redacted, rerouted on-prem or only detected) | `RequestId`, `Action`, `Categories`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId`, `Endpoint` |
| 1003 | `gateway.request.refused` | Gateway | A request was refused by policy: attachment not allowed, model or provider not allowed for the key | `RequestId`, `ErrorCode`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId`, `Endpoint` |
| 2001 | `admin.change` | Admin API | Every audit log entry (key created, revoked, rotated or revealed; provider, budget, rule or config changed) | `Actor`, `Action`, `EntityType`, `EntityId` |
| 3001 | `data.read` | Data API | Every authenticated read | `Client`, `Path`, `Status`, `Cursor` |

`Reason` for `gateway.auth.failed` is one of `missing_key`, `invalid_key`, `key_revoked`, `key_expired`,
`key_disabled`, `owner_inactive`. `PiiCategories` are counts per category (`Personnummer:2,Email:1`), never the
values. Audit `Details` (before and after values) are **not** included in `admin.change`; they stay in the audit
log, where secrets are already masked.

### Authentication failures: aggregated, not one row per request

Requests with a missing or unknown key are **unauthenticated**, so writing a row per request would let anyone fill
the database by sending made-up keys. The gateway therefore counts failures in memory per bucket:

> bucket = (reason, endpoint, key id if the key was recognised, source address)

Every `Gateway:Security:AuthFailureFlushSeconds` (default 10) the buckets are written to the new
**`AuthFailures`** table (one row per bucket with `Count`, `FirstSeen`, `LastSeen`) and one
`gateway.auth.failed` event is logged per bucket. At most `Gateway:Security:MaxAuthFailureBuckets` (default 1 000)
distinct buckets are kept per interval; failures beyond that are folded into one overflow bucket per reason and
endpoint with no key or address. The table and the log volume are therefore bounded no matter how much traffic
arrives.

Failures for a **known** key (revoked, expired, disabled, owner inactive) carry the key id, team and department. A
revoked key that is still being used is one of the most useful signals for security: either the key leaked or an
integration was forgotten.

The plaintext of a presented key is never stored or logged. `KeyPrefix` is only set for recognised keys (it is the
prefix shown in the admin UI); for unknown keys nothing derived from the presented value is kept, because people
sometimes send the wrong secret (for example a provider key) to the gateway.

### Source address

The client address is personal data in many jurisdictions, so how much of it is kept is a setting:

| `Gateway:Security:SourceAddress` | Stored and logged |
|---|---|
| `Truncated` (default) | IPv4 /24 (`192.0.2.0`), IPv6 /48 |
| `Full` | The full address |
| `None` | Nothing |

Behind a reverse proxy the gateway only sees the proxy's address unless forwarded headers are enabled
(`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `UME_FORWARDED_HEADERS=true` in the Compose files). Only
enable this when the gateway cannot be reached except through the proxy, or the header can be spoofed.

### Shipping to a SIEM

The gateway and admin API already export logs, traces and metrics over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is
set. `deploy/otel/collector-siem.example.yaml` is a starting point for an OpenTelemetry Collector (contrib
distribution) that:

1. receives OTLP from both services,
2. keeps only records from the `Ume.LlmGateway.Security` category in the SIEM pipeline,
3. exports them to syslog (RFC 5424; works with QRadar, ArcSight and most SIEMs), with commented-out exporters for
   Splunk HEC, Elastic and Azure Monitor (Microsoft Sentinel).

Push over OTLP is best-effort: if the collector is down, events can be lost. `AuthFailures`, `UsageRecords` and
`AuditLog` are the durable record; the Data API's `security.read` feeds let a SIEM catch up from them.

## Phase 2: Data API

`src/Ume.LlmGateway.DataApi` is a separate, optional service. An adopter can deploy it in another network zone, point
it at a read replica (`ConnectionStrings:gatewaydb`), or not deploy it at all. Deployment and identity-provider setup
are in the [runbook](runbook.md#data-api-optional); the contract is the OpenAPI document at `/openapi/v1.json`
(public; it contains no data) with a browsable reference at `/scalar`.

### Access

- **Read-only, enforced by the database.** The service connects as `ume_data`, which has `SELECT` only. Tables that
  hold secrets are granted column by column, so the role cannot read key hashes, encrypted key secrets, provider
  credentials or base URLs, or the Data Protection key ring. A bug in the API cannot leak them. The tests run as this
  role with the real `app-roles.sql`.
- **Machine authentication.** OAuth 2.0 client-credentials access tokens from the adopter's IdP
  (`DataApi:Authority`, audience `DataApi:Audience`, default `ume-data-api`). Each integration is its own client,
  with its own audit trail (`data.read` events), rate limit (`DataApi:RequestsPerMinute`, default 600) and revocation.
- **Permissions are consumer classes**, read from the claims in `DataApi:PermissionClaims` (default `scope`, `scp`
  and `roles`). This covers Keycloak (realm roles or client scopes), AD FS and Entra ID, which puts application
  permissions in `roles`.

| Permission | Endpoints | Data |
|---|---|---|
| `usage.aggregate` | `GET /v1/usage/aggregate?from=&to=&granularity=day\|month` | Totals per period, department (with cost-centre code), model, provider, residency and endpoint: requests, successful requests, tokens, cost in SEK and USD. No key- or team-level rows; small departments folded (see phase 3). `from`/`to` are inclusive dates in `DataApi:TimeZone`. |
| `usage.detail` | `GET /v1/usage/records` (feed) | One row per request with key, team and department ids, model, provider, tokens, cost, latency, outcome, routing rule. PII categories only if `DataApi:Detail:IncludePiiCategories`. |
| `security.read` | `GET /v1/security/auth-failures`, `/requests`, `/audit` (feeds); `GET /v1/security/keys` | Refused keys; requests with a PII action or a policy refusal (with PII categories); the admin audit log with its masked before/after details; every key's status, expiry, last use and policies. |
| `catalog.read` | `GET /v1/catalog/departments`, `teams`, `keys`, `providers`, `models`, `prices`, `budgets`, `exchange-rates` | Dimensions for joining. Keys are id, team, name and prefix only. |

### Incremental feeds

A feed returns rows in id order after a cursor: `GET /v1/usage/records?after=<cursor>&limit=5000` answers
`{ items, nextCursor, hasMore }` (also as `X-Next-Cursor` and `X-Has-More` headers). The integration stores
`nextCursor` and passes it as `after` on the next run; omit `after` to start from the beginning. `hasMore: false` means
caught up for now. `limit` defaults to 1 000 (maximum `DataApi:MaxPageSize`, 10 000).

Ids alone are not a safe cursor. Several gateway instances insert batches concurrently, so a row with a lower id can
commit after a row with a higher one, and a consumer that has already moved past it would never see it. Every fed table
therefore has a `RecordedAt` column set by Postgres (transaction start). A feed hands out only rows older than
`DataApi:SettleSeconds` (60), and **stops at the first row that has not settled**, even when later rows have. A
row is skipped only if a write transaction runs longer than the settle window; the usage writer's transactions take
milliseconds.

### Formats

JSON by default; `?format=csv` or `?format=ndjson` (or the `Accept` header `text/csv`, `application/x-ndjson`) for
bulk loads. Many ETL tools cannot set headers, hence the query parameter. CSV is RFC 4180 with a header row, lists
joined with `;`, and cells a spreadsheet would run as a formula prefixed with `'`.

Example (PowerShell, Keycloak):

```powershell
$token = (Invoke-RestMethod -Method Post "$authority/protocol/openid-connect/token" -Body @{
  grant_type = 'client_credentials'; client_id = 'bi-etl'; client_secret = $secret }).access_token
Invoke-RestMethod "https://data.example.se:10443/v1/usage/aggregate?from=2026-10-01&to=2026-10-31&granularity=month" `
  -Headers @{ Authorization = "Bearer $token" }
```

Feeds map to warehouse tables as a star schema: `fact_usage` (records), `fact_usage_daily` (aggregate),
`dim_department`, `dim_team`, `dim_key`, `dim_model`, `dim_budget`. A sample Data Factory / SSIS pipeline and a Power
BI template are good follow-ups.

Not planned: direct database access, database views as a contract, or OData. Direct access and views tie consumers to
Postgres and the schema; OData would add a large dependency for a feature Power BI also gets from the warehouse.

## Phase 3: governance settings

Settings, with defaults chosen so that an adopter who does nothing exposes the least:

| Setting | Default | Effect |
|---|---|---|
| `DataApi:MinimumGroupSize` | 5 | **Implemented.** In each period, departments with fewer distinct keys than this are reported together without a department (`departmentId` null), so a one-person team's use is not visible as personal performance data. |
| `DataApi:Detail:IncludePiiCategories` | `false` | **Implemented.** Whether `usage.detail` includes PII category counts (always included for `security.read`). |
| `Retention:UsageDays`, `Retention:AuthFailureDays`, `Retention:AuditDays` | unset (keep) | **Proposal.** A purge job in the migration service deletes older rows. Each adopter sets these from its records-retention decision. |

Adopters document in their DPIA: which consumers get which scope, retention, minimum group size, source address
mode, and where the SIEM and warehouse copies are kept (each copy is a new record with its own retention).

## Open points

- Unknown keys are now remembered for `Gateway:KeyCacheSeconds`, so repeating the same wrong key costs no query.
  A flood of *different* made-up keys still costs one query each. Limiting refused keys per client network before
  the lookup would stop that, but organisations behind one NAT address share a network, so it needs a deliberate
  threshold; not done.
- Whether to align attribute names with a SIEM schema (OCSF or Elastic Common Schema). The collector can rename
  attributes, so this can wait for a concrete adopter.
