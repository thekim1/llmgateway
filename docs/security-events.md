# Security events and data for other teams

Security teams, BI and management get the gateway's data through interfaces, never through its database. The plan
and the reasons are in [data-access.md](data-access.md). What exists today:

- **Security events for your SIEM.** The gateway and admin API log security events under the category
  `Ume.LlmGateway.Security` with stable event names: `gateway.auth.failed`, `gateway.pii.action`,
  `gateway.request.refused` and `admin.change`. Set `OTEL_EXPORTER_OTLP_ENDPOINT` on both services and route that
  category to your SIEM with an OpenTelemetry Collector.
  [`deploy/otel/collector-siem.example.yaml`](../deploy/otel/collector-siem.example.yaml) sends it to syslog (RFC 5424),
  with Splunk, Elastic and Sentinel alternatives.
- **Refused keys are recorded.** Missing, unknown, revoked, expired and disabled keys are counted in the new
  `AuthFailures` table. A row covers one reason, endpoint, key and client network for up to 10 seconds, so callers
  without a valid key cannot flood the database. The presented key is never stored. Refused keys on `GET /v1/models`
  are recorded with the endpoint `Models`.
- **Floods of unknown keys are throttled.** After `Gateway:AuthFailuresPerMinute` (60) missing or unknown keys in a minute
  from one client address (IPv6 per /64), the gateway answers that address `429 rate_limited` without looking keys up
  until the minute ends; keys it has cached keep working, so other clients behind the same proxy are not cut off. Set
  trusted proxies (see [client addresses](client-addresses.md)) so the limit applies per client, not per proxy.
- **Logs are JSON outside development.** The services write console logs with the JSON formatter unless
  `Logging:Console:FormatterName` says otherwise, so a line break or terminal escape sequence in a client-supplied value
  (a model name, a Data API path) cannot start a forged log line. Those values are also logged and stored with control
  characters replaced by `?`. Set `Logging__Console__FormatterName=simple` for the old plain-text format.
- **Unknown keys are cached.** A key the gateway does not know is remembered for `Gateway:KeyCacheSeconds` (30 s), so a
  client retrying with a deleted or mistyped key costs no database query (0.99 → 0.12 ms per refused request in the
  `audit` benchmark). Creating a key in the admin API clears the cache at once.

Settings (`Gateway:Security:*`):

| Setting | Default | |
|---|---|---|
| `SourceAddress` | `Truncated` | Client address kept for refused keys: `Truncated` (IPv4 /24, IPv6 /48), `Full` or `None`. Behind a reverse proxy, list the proxy under *Settings > Trusted proxies* in the admin UI, or every address is the proxy's. |
| `AuthFailureFlushSeconds` | `10` | How often refused keys are written and logged. |
| `MaxAuthFailureBuckets` | `1000` | Distinct rows per interval; anything beyond is folded into one row per reason and endpoint. |

## Data API for BI, management and security

An optional, read-only HTTPS service (`Ume.LlmGateway.DataApi`) for integrations: a data warehouse ETL job, a
management dashboard, a SIEM connector. It never gives anyone database access.

- **Each integration is an OAuth client** (client credentials) at your identity provider and gets only the
  permissions it needs: `usage.aggregate` (totals per day or month and department, model, provider), `usage.detail`
  (one row per request), `security.read` (refused keys, PII actions, audit log, key inventory) or `catalog.read`
  (departments, teams, keys, models, prices, budgets). Works with Keycloak, AD FS and Entra ID.
- **Or a Data API key**: a gateway admin creates one under *Access → Data API access* in the admin UI, ticks the
  permissions and gets the key (`ume-dk-…`) once, with an example request. Send it as `Authorization: Bearer` or
  `X-Api-Key`. Revoking takes effect within 30 seconds (`DataApi:KeyCacheSeconds`).
- **Incremental feeds**: pass the `nextCursor` of one page as `after` on the next call. JSON, or `format=csv` /
  `format=ndjson` for bulk loads.
- **Privacy by default**: departments with fewer than 5 active keys in a period are reported together without a
  department, and per-request PII categories are left out of `usage.detail`.
- **Read-only by database role**: it connects as `ume_data`, which cannot read gateway key hashes, encrypted secrets
  or provider credentials at all (it reads only the hashes of Data API keys and writes only their last use). Every read is logged as a `data.read` security event.

Locally it starts with the rest of the stack (resource `dataapi`, `https://localhost:7311/scalar`). Get a token from the
bundled Keycloak with the `ume-data-dev` client (all four permissions); its secret is the AppHost parameter
`data-client-secret`:

```powershell
$token = (Invoke-RestMethod -Method Post "<keycloak-url>/realms/ume/protocol/openid-connect/token" -Body @{
  grant_type = 'client_credentials'; client_id = 'ume-data-dev'; client_secret = '<data-client-secret>' }).access_token
Invoke-RestMethod "https://localhost:7311/v1/usage/records?limit=10" -Headers @{ Authorization = "Bearer $token" }
```

In production it is the Compose profile `data` (`COMPOSE_PROFILES=data`); see the runbook, *Data API (optional)*,
and for existing installations the upgrade guide. Endpoints and design: [data-access.md](data-access.md#phase-2-data-api).
