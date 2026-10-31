# Upgrade guide and release checklist

Each release says whether it supports a **rolling update** ([Updates without downtime](zero-downtime-updates.md)): the
previous version keeps running against the new schema while its instances are replaced. "No" means a planned stop: stop
the gateway, admin API and Data API, migrate, start the new version.

## POC 0.1.0

Initial demonstrator: .NET 10.0.12, Aspire 13.6, Vue 3, PostgreSQL 17 and Redis 8.6.
Production deployment now uses chiseled-extra images, immutable base digests, file-mounted
secrets, verified TLS, restricted DB roles/Redis ACL, static SPA serving and segmented Compose.
See [runbook](runbook.md) for the actual deployment procedure.

## 0.8.7: SEK leftovers dropped, .NET 10.0.14 security update

- **Rolling update: no** (drops columns 0.8.6 reads). Stop the gateway, admin API and Data API, run the migration service
  (migration `DropSekLeftovers`), then start the new version.
- **Dropped:** the `CostSek` columns of `UsageRecords` and `UsageMonthlyTotals` and the `ExchangeRates` table. Nothing has
  read or written them since 0.8.1; all costs are in `CostUsd`. Export them first if you want to keep the old SEK figures.
- **Data retention:** the migration also redefines `ume_retention_step` without `CostSek`; owner and grants stay as they are.
- **Database roles:** `app-roles.sql` (Compose, Kubernetes and Portainer) no longer names `ExchangeRates`. The migration
  service applies it on start; if you run the role grants yourself, use the new file **after** the migration, because the
  old one grants a table that no longer exists.
- **Rolling back:** migrating down recreates the columns (0) and an empty `ExchangeRates` table, so migrating further back
  past `BudgetsInUsd` (0.6.0) converts budgets with the fallback rate of 10 SEK per USD instead of the stored rates.
- **Security update:** the images are built on .NET 10.0.14 (`aspnet:10.0.14-noble-chiseled-extra`), which fixes four
  CVEs; Aspire 13.6.4 and OpenTelemetry 1.20.1. Nothing to configure.
- **Log categories** of single classes have new names (code grouped per feature): a level set for one class needs the
  new name, for example `Logging__LogLevel__Ume.LlmGateway.Gateway.Requests.GatewayRequestHandler` (was
  `...Gateway.Pipeline...`). Per-project levels and security events are unchanged.

## 0.8.6: a provider is deleted together with its models

- **Rolling update: yes.** No migration and no configuration change.
- **New:** *Delete* on a provider with models deletes them with it (*Delete provider and N models*), in one transaction;
  refused while a route or routing rule uses one of them. API: `DELETE /api/providers/{id}?deleteModels=true`; without
  the flag the call is 409 as before. See [Providers and models](providers-and-models.md#removing-a-provider).

## 0.8.5: Claude on the Foundry provider authenticates

- **Rolling update: yes.** No migration and no configuration change.
- **Fixed:** Claude deployments of an Azure AI Foundry provider with an API key failed with 401 (502 to the client): the key
  went to the resource's Anthropic API in `api-key`, which it does not read. It now goes in `x-api-key`.
- **Remove workarounds:** a separate *Anthropic* provider kept only for Foundry's Claude deployments is no longer needed;
  see [Providers and models](providers-and-models.md).

## 0.8.4: model prices from models.dev, Claude on the Foundry provider, team admins, audio priced per unit

- **Rolling update: yes.** Four migrations, each adding nullable or defaulted columns or widening one, so the previous
  version keeps working against the new schema while its instances are replaced: `AudioDurationPrecision`, `TeamCodes`,
  `ModelListPrices`, `ModelBaseModel`.
- **Check before upgrading:** prices of existing Anthropic, Ollama Cloud, OpenAI and OpenRouter models follow models.dev
  from two minutes after the admin API starts (see below), the admin API needs outbound HTTPS to `models.dev`, and known
  provider types now serve every endpoint of their API whatever was ticked.

### Model prices and details from models.dev

- **Rolling update: yes.** Migration `ModelListPrices` adds `ModelDeployments.AutoUpdatePrice` and
  `ProviderAccounts.AutoUpdatePrices` (both default `true`), `ModelDeployments.Description` and `ModelPrices.Source`; the
  previous version ignores them.
- **Changed behaviour: prices of existing models can change.** Two minutes after the admin API starts, models of
  Anthropic, Ollama Cloud, OpenAI and OpenRouter providers whose upstream name is in the models.dev list get the list
  price added to their price history when it differs from theirs (usage before keeps its cost), and empty descriptions,
  capabilities and context windows are filled. A replaced price is in the price history and the audit log
  (`system:price-list`). For an agreed price, turn off *Price from models.dev* on the model (or the provider) and add it
  again. Such models no longer take a price by hand while they follow the list (`POST /api/models/{id}/prices` answers 409).
- **Azure AI Foundry: one provider for Claude too.** Migration `ModelBaseModel` adds the nullable
  `ModelDeployments.BaseModel` (the model behind a deployment). A Foundry provider now discovers and serves Claude
  deployments (through the resource's `…/anthropic/v1`), so a separate Anthropic provider per resource is no longer
  needed. Existing Anthropic providers at a Foundry address keep working unchanged. To move to one provider, add the Claude
  deployments under the Foundry provider with *Discover models*, point routes at them, then delete the Anthropic one.
  Claude models added by hand under their own deployment name need *Base model* set (`claude-…`); until then they go to
  `…/openai/v1`, which does not serve Claude. Rolling update: yes; an old gateway ignores the column.
- **Azure deployments** now get descriptions, capabilities and context windows from models.dev, and a price from it when
  they have none (discovery and the daily update). Their prices are otherwise left alone. Models added before have no
  base model: one whose deployment name differs from its model (`prod-chat`) is not found in models.dev until it has one.
  Open *Discover models* on the Foundry provider and use *Update … shown*, which sets the base model and fills empty
  descriptions of models already added.
- **Provider capabilities follow from the provider type.** OpenAI, Azure OpenAI and Foundry providers now serve every
  OpenAI endpoint (Responses, speech to text, realtime, images), Anthropic chat and Messages, Ollama chat and embeddings,
  Ollama Cloud chat, whatever was ticked before; which model gets a request still follows its kind. Only OpenAI-compatible
  providers keep their chosen endpoints. A provider that had an endpoint unticked to keep requests away from it now gets
  them for models of that kind: disable or remove those models instead. `capabilities` on `POST`/`PUT /api/providers` is
  now optional and ignored except for `OpenAICompatible`. No migration.
- **Outbound HTTPS** from the admin API to `models.dev` (about 6 MB once a day). Without internet access, set
  `Admin__PriceListUrl` to a copy of `https://models.dev/api.json` in your network, or empty to turn it off; a failed
  download only logs a warning and leaves prices as they are.
- **Admin API:** `Model` has `description`, `autoUpdatePrice`, `priceMode` and `priceListKey`; `Provider` has
  `autoUpdatePrices` and `priceList`; `Price` has `source`; `DiscoveredModel` has `description` and `baseModel`, and `Model`
  `baseModel`; catalogue models have `description`
  and `features`. `POST`/`PUT /api/models` and `/api/providers` take the new settings (omitted keeps them).
  `GET`/`POST /api/settings/model-prices` are new. The configuration export includes the settings, descriptions and
  model capabilities.

### Teams manage their own keys

- **Rolling update: yes.** Migration `TeamCodes` adds the nullable `Teams.Code` column and a unique index on it; the
  previous version ignores both. While old admin API instances still run, a team admin's calls to them get 403.
- **Nothing changes until you opt in.** No team has a code after the upgrade, so no one is a team admin. To start: give
  a team a **Team code** in *Organisation*, then give its members the `team-admin` role and that code in the `teamCodes`
  claim, or map groups (`UME_OIDC_TEAM_CLAIM`, `UME_OIDC_GROUPS_TEAM_ADMIN`; add both to `deploy/.env` and the compose
  file you deploy with, as in `deploy/compose.prod.yaml`). See [Identity provider and roles](identity-and-roles.md#team-scope).
- **Admin API:** `Team` has a new `code` field, and `POST`/`PUT /api/teams` take it; `/bff/user` returns `teamCodes`.
  Existing clients that do not send `code` leave it unchanged.

### Input audio priced per second, minute or hour

- **Rolling update: yes.** Migration `AudioDurationPrecision` widens `ModelPrices.AudioPerMinuteUsd` from
  `numeric(18,6)` to `numeric(18,10)`; the old version reads and writes the wider column unchanged. `ModelPrices` is
  small, so the rewrite is instant.
- **Changed behaviour:** a model with `audioPerMinuteUsd` above 0 and `audioInputPerMillionUsd` at 0 bills its audio
  input tokens by the duration only, no longer also at `inputPerMillionUsd`. Whisper-style models report no audio tokens
  and cost the same as before. A model whose provider charges both duration and audio tokens needs an audio input token
  price.
- The admin API is unchanged: prices are still sent per minute. To enter a price per hour, divide it by 60 (or use the
  unit in the admin UI price form).

## 0.8.3: tool calls on gpt-5.6 and GPT-6 through Chat Completions

- **Rolling update: yes.** No migration and no configuration change.
- **Changed behaviour:** a Chat Completions request with `tools` to gpt-5.6 or GPT-6 (reasoning profile), and not setting
  `reasoning_effort: "none"`, is now sent to the provider's `/v1/responses` and answered in Chat Completions form. Before,
  the provider refused it with a 400. The provider must serve `/v1/responses` (Azure OpenAI and Foundry v1 do; the
  provider's *Responses* tick is not needed for this). See [Providers and models](providers-and-models.md).
- **Remove workarounds:** a team or key policy that forces `reasoning_effort: "none"` for these models is no longer
  needed, and on gpt-6-astra and gpt-6.1-sol it makes every request fail (they reject `none`). Clients set to
  `api_mode: responses` only for these models can go back to Chat Completions.

## 0.8.2: provider time to first token, failed requests in Usage

- **Rolling update: yes.** The migration `ProviderFirstToken` adds a nullable `ProviderFirstTokenMs` column to
  `UsageRecords`; older instances keep writing records without it while they are replaced.
- **Changed figures:** provider p50/p95 on Overview, Health & operations and Provider stability are now the provider's
  time to first token on streamed answers, not the whole request's latency, so they are much lower and no longer grow with
  the length of the answers. Requests recorded before the upgrade have no such figure, so right after it the columns show
  *—* until streamed traffic arrives.
- **Admin API rename:** `p50LatencyMs`/`p95LatencyMs` on `GET /api/ops/health` and `GET /api/ops/providers/stats` are now
  `p50FirstTokenMs`/`p95FirstTokenMs`. Update scripts that read them.
- **New fields and filters** (additive): `providerFirstTokenMs` on `GET /api/usage/requests` and the Data API's
  `/v1/usage/records`; `GET /api/usage/requests` takes `errors=true`, `model` and `provider`; metric
  `ume.gateway.provider.time_to_first_token`.
- The old `CostSek` columns and the `ExchangeRates` table are still kept in this release; they are dropped in a later one.

## 0.8.1: costs in USD only, SEK removed

- **Rolling update: yes.** The migration `SekNoLongerWritten` only gives the `CostSek` columns of `UsageRecords` and
  `UsageMonthlyTotals` a default of 0, so 0.8.0 keeps writing them while its instances are replaced. Nothing reads them
  any more; they and the `ExchangeRates` table are dropped in the next release (which will be **Rolling update: no**
  from 0.8.0, so upgrade through 0.8.1).
- **Breaking: SEK is gone from every interface.** Update whatever reads these before upgrading:
  - Gateway response headers `x-ume-cost-sek` and `x-ume-budget-remaining-sek`: use `x-ume-cost-usd` and
    `x-ume-budget-remaining-usd`.
  - Budget alert webhook: `spentSek` and `limitSek` are gone; use `spentUsd` and `limitUsd`.
  - Data API: `costSek` is gone from `/v1/usage/records` and `/v1/usage/aggregate` (use `costUsd`), `limitSek` from
    `/v1/catalog/budgets` (use `limitUsd`), and `/v1/catalog/exchange-rates` is removed. Convert to SEK in your report
    where needed.
  - Metric `ume.gateway.cost` (SEK) is removed; use `ume.gateway.cost.usd`.
  - Admin API: `GET`/`PUT /api/settings/exchange-rate` are removed (404), and so is the Settings card.
- **Configuration:** `Gateway:FallbackSekPerUsd` (`Gateway__FallbackSekPerUsd`) is no longer read; remove it from your
  settings. The gateway's readiness no longer has an `exchange-rate` check, so it is no longer Degraded without a rate.

## 0.8.0: admin UI menu and phone layout, costs in USD

- **Rolling update: yes.** No migrations; 0.7.1 runs side by side while its instances are replaced.
- **Usage CSV export in USD.** The admin UI's export (`/api/usage/export.csv`) has the column `kostnad USD` instead of
  `kostnad SEK`. Update spreadsheets or imports that read the column by name, and convert to SEK yourself where needed.
  The Data API, gateway response headers and budget webhooks still carry SEK next to USD.
- **Admin API:** usage summaries and the catalogue report USD (`totalCostUsd`, `costUsd`, `inputUsdPerMillion`,
  `outputUsdPerMillion`); request rows no longer have `costSek`. The catalogue no longer needs an exchange rate.
- **Developers:** each npm project has an `.npmrc` that turns off install scripts and only takes package versions that are
  at least 7 days old when you add or update a package; `npm ci` installs the lockfile as before. See
  [npm packages](security-and-compliance.md#npm-packages).

## 0.7.1: Redis ACL fix for admin sign-in

- **Rolling update: yes.** No migrations; 0.7.0 runs side by side while its instances are replaced.
- **Redeploy the Redis ACL.** Admin sessions are written with a time to live (`SETEX`/`PSETEX`), which the ACL shipped
  with 0.6.4 and 0.7.0 did not allow, so admin sign-in failed with a 500 (`RedisServerException`) right after the
  identity provider returned. Redeploy the stack (Portainer regenerates `users.acl` on every start) or run
  `init-deployment.sh` and restart Redis (Compose); Kubernetes: give the Redis user `gateway` the commands in
  `deploy/redis/users.acl.example`.

## 0.7.0: content guard, token quotas, usage journal, retries, rate limits, request policies, prompt caching

- **Rolling update: yes,** with the caveats below. Five additive migrations: `CacheWritePrice`, `KeyConcurrencyLimit`,
  `RequestPolicies`, `TokenBudgets` and `ContentGuard`; 0.6.4 runs against the new schema while its instances are replaced.
- **Usage journal: redeploy the Redis ACL.** The gateway user needs `zrangebyscore` (read-only) to take over usage batches
  of a stopped instance: redeploy the stack or run `init-deployment.sh` (Compose) so `users.acl` is regenerated from
  `deploy/redis/users.acl.example`, then restart Redis; Portainer regenerates it on redeploy. Without it the journal still
  works (no records are lost while the database is down) but batches of a stopped instance wait until it is back, and the
  log warns "Usage journal unavailable (… NOPERM …)". The journal's keys are `ume:usage-journal*` (inside `~ume:*`).
- **Content guard:** migration `ContentGuard` adds `VirtualKeys.ContentGuardPolicy` (default `Off`, so nothing changes)
  and `UsageRecords.GuardFlags`. Nothing to configure unless you want it (docs/content-guard.md).
- **Token quotas:** migration `TokenBudgets` adds `Budgets.Unit` (default `Usd`, so existing budgets are unchanged),
  `LimitTokens` and `Residency`, and `AlertEvents.SpentTokens` / `LimitTokens`. During a rolling update an old gateway
  instance does not enforce token quotas (it ignores the columns); create them after the rollout. The Data API budgets
  feed adds `unit`, `limitTokens` and `residency`.

- **Migrations:** `CacheWritePrice`, `KeyConcurrencyLimit` and `RequestPolicies` only add nullable
  columns (`ModelPrices.CacheWriteInputPerMillionUsd`, `VirtualKeys.MaxConcurrentRequests`, `VirtualKeys.RequestPolicy` and
  `Teams.RequestPolicy`, jsonb); the previous version ignores them. One caveat: until every admin API instance runs the new
  version, an old instance cannot read usage records of the new endpoint `AnthropicCountTokens` (usage and PII pages may
  fail on it), so finish the rollout before clients use `/v1/messages/count_tokens`.
- `app-roles.sql` grants the Data API role `MaxConcurrentRequests` on `VirtualKeys`; the migration service applies it on
  its next start (the Data API keys feed gains `maxConcurrentRequests`).
- **Behaviour changes:** per-minute limits slide (a key that used its whole limit at the end of one minute waits into the
  next, and refused requests no longer count); the last candidate is retried once after a connection error or 502/503/504
  (`Gateway__Retry__MaxRetries=0` restores the old behaviour); OpenAI-format requests translated for Claude carry prompt-cache
  markers (`Gateway__PromptCaching=Off` restores the old behaviour; with Claude add a *Cache write* price, see below).
- **Redis:** the new keys `ume:cooldown:*` are inside the existing ACL (`~ume:*`); the cooldown script uses `set` and
  `pexpire` (already granted). Needs Redis 7 or later (`PEXPIRE … GT`); the shipped images use 8.6.
- **Behaviour change:** a 429/503 with `Retry-After` no longer counts toward the provider's circuit breaker; the deployment
  is put on cooldown instead (`CircuitBreaker__MaxRetryAfter=00:00:00` restores the old behaviour).
- Existing Claude prices bill cache writes at the input price, as before; add a price with *Cache write* to bill them
  at Anthropic's rate from its effective date.

## 0.6.4: security hardening

- **Rolling update: yes.** Two additive migrations (`ProviderFeaturePolicy`, `AuditActorContext`); 0.6.3 runs against the
  new schema while its instances are replaced.
- **Everyone is signed out once.** Admin sessions move to Redis (`ume:admin-session:*`, covered by the existing `~ume:*`
  ACL; commands `get`, `setex`, `psetex`, `del`). The ACL shipped with 0.6.4 and 0.7.0 lacked `setex`/`psetex`, so admin
  sign-in fails with a 500 (`RedisServerException`): redeploy with the current `deploy/redis/users.acl.example`. Old cookies are not accepted after the upgrade; users sign in
  again. During a rolling update a user can be signed out by an old instance once more.
- **New settings (defaults are fine):** `Admin:MaxSessionHours` (12) and `Gateway:AuthFailuresPerMinute` (60).
- **Provider address edits** need the credential again when the host, scheme or port changes. Scripts that move a provider
  with `PUT /api/providers/{id}` or a configuration import must send `credential` (or `credentialEnvironment`).
- **PII policy:** keys with *Block* or *Redact* may now refuse or change requests that passed before (personal data in the
  `user` field, a message `name`, an Anthropic text document, or written with full-width digits or invisible characters).
- **Console logs are JSON** outside development (all services). Log shippers and alerts that parse the plain-text format
  (`warn: Category[id]` lines) must read JSON, or set `Logging__Console__FormatterName=simple`.
- **Provider refusals are trimmed.** A provider's 4xx reaches the client with only `message`, `type`, `code` and `param`;
  clients that read other fields of a provider's error body no longer get them.
- **Repeated JSON properties** (`{"model":"a","model":"b"}`) get `400 invalid_request` instead of a `500`; on live audio
  such an event, or one that is not valid JSON, gets an `error` event instead of being passed to the provider.
- **API reference:** `/openapi/v1.json` and `/scalar` on the gateway and Data API stay public by default; turn them off with
  `Gateway:PublishApiReference=false` / `DataApi:PublishApiReference=false`. `/scalar` now has its own CSP.
- **Narrower database grants,** applied by the migration service on start (no manual step). The gateway role can no longer
  read `AuditLog` or `DataApiKeys` or add Data Protection keys; the admin role can no longer insert or delete the settings
  row or change `UsageRetainedFrom`. During a rolling update the old gateway version needs nothing it loses.
- **Internal CA:** new installations get an RFC 5280-conformant CA. On an existing Compose installation run
  `deploy/init-deployment.sh --renew-ca` once (it needs no other options) and restart the stack; Portainer replaces the CA
  on its own at the next redeploy. Integrations that trusted the old `ca.pem` must get the new one. Afterwards move
  `deploy/certs/ca.key` off the host.
- **Provider tools and storage are off for every key** (migration `ProviderFeaturePolicy` adds two columns, default
  `false`; rolling update still works). Clients that use web search, file search, code interpreter, MCP or other provider
  tools get `400 tool_not_allowed`, and `store: true`, `background` or `conversation` get `400 storage_not_allowed`, until
  an admin turns on *Allow provider tools* / *Allow provider storage* on the key. Responses API requests are now sent with
  `store: false`, so `previous_response_id` stops working for keys without provider storage. Check which applications
  rely on these before upgrading.
- **Audit entries record more** (migration `AuditActorContext` adds seven nullable columns to `AuditLog`; rolling update still
  works). New setting `Admin:AuditSourceAddress` (`Full`; `Truncated` or `None` if your privacy assessment requires).
  The admin API applies the gateway's trusted proxies to find the admin user's address. The Data API audit feed has the new
  fields, and its CSV has seven new columns at the end: consumers that read columns by position must allow for them.
- **New admin security events:** `admin.signin`, `admin.signout`, `admin.session.extended`, `admin.access.denied`,
  `admin.data.read`; exports are `admin.change` with action `export`. Route them in the collector like `admin.change`.

## 0.6.0: updates without downtime, budgets in USD

- **Rolling update: no** (renames budget columns 0.5.0 reads). Stop the gateway, admin API and Data API, migrate, start the
  new version. Later releases can use the rolling update described below.
- **Budgets are in USD.** The migration `BudgetsInUsd` renames `Budgets.LimitSek` to `LimitUsd` and `AlertEvents.SpentSek`
  / `LimitSek` to `SpentUsd` / `LimitUsd`, and converts the amounts: budget limits at the USD exchange rate in effect at
  migration time (so each budget allows what it allowed just before the upgrade; with no rate entered, the default
  fallback of 10 SEK per USD), stored alerts at the rate in effect when they were raised. Check the converted limits on
  *Budgets & alerts* and round them if you like. Spend is now summed from `CostUsd`.
- The gateway's Redis spend counters move from `ume:spend:*` (micro-SEK) to `ume:spend-usd:*` (micro-USD). The new
  counters are seeded from the usage records on first use; the old ones are never read again and expire with their
  period. No Redis ACL change is needed (`~ume:*`).
- **API changes.** Admin API budgets and alerts use `limitUsd` / `spentUsd` instead of `limitSek` / `spentSek`. The
  gateway adds `x-ume-cost-usd` and `x-ume-budget-remaining-usd`; `x-ume-cost-sek` and `x-ume-budget-remaining-sek` stay
  (converted at the current rate). The alert webhook adds `spentUsd` / `limitUsd` and keeps `spentSek` / `limitSek`. The
  Data API budgets feed adds `limitUsd` and keeps `limitSek` (converted at the current rate). A budget-exceeded message
  states the limit in USD.
- **Error messages are in English** in the gateway (`error.message`, realtime `error` events), the admin API and the
  Data API (`detail`, `title`, field errors). Error **codes** are unchanged; clients that match on message text must match
  the code instead.
- Every service now fails `/health/ready` from the moment it is told to stop and accepts the new setting
  `Shutdown__DrainSeconds` (default 0, no change). Load balancers that poll readiness see a stopping instance as down; a
  Compose `healthcheck` may briefly show a stopping container as unhealthy.
- New, opt-in: `compose.rolling.yaml` and `rolling-update.sh` for Docker Compose, and Kustomize manifests in
  `deploy/kubernetes`. A single-instance `compose.prod.yaml` installation needs no change.
- The first rolling update to this version still stops instances of 0.5.0, which do not drain; a few POSTs can fail at the
  moment each old instance stops.
- The admin API now asks `api.github.com` for the newest release (shown in *Settings > Version*), at most every 6 hours. Without
  outbound HTTPS it logs a warning and the card says it could not reach GitHub; set `Admin__CheckForReleases=false` to stop it.

## 0.5.0: request logging

- **Rolling update: yes** (from 0.4.1).
- **Successful requests are no longer written to the gateway log.** Refused, failed and cancelled requests still are, and
  every request is still a usage record. If dashboards or alerts count the "Request … status … in … ms" log lines, switch
  them to the usage data or the `ume.gateway.requests` metric, or turn on *Settings > Request logging*.
- The migration `RequestLogSwitch` adds `GatewaySettings.LogAllRequests` and `LogAllRequestsUntil`; it is additive, so the
  previous gateway and admin API keep working against the new schema. No grant changes.
- The usage writer now writes with `COPY`; it needs only the existing `INSERT` grant on `UsageRecords`.

## 0.4.1: MCP gateway removed

- **Rolling update: no** (drops columns 0.4.0 reads).
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

## 0.4.0: usage dashboard

- **Rolling update: yes** (from 0.3.x; all three 0.4.0 migrations are additive).
- The migration `RequestTimings` adds the nullable columns `UsageRecords.UpstreamMs` and `TimeToFirstTokenMs` (no table
  rewrite). It is additive: the previous gateway keeps writing records without them. The existing grants cover the new
  columns (the gateway inserts and the Data API reads the whole table).
- Records written before the upgrade have no provider time or time to first token, so the *Gateway overhead* and
  *Time to first token* cards fill in from the upgrade on.
- The Data API usage feed and `GET /api/usage/requests` have the new fields `upstreamMs` and `timeToFirstTokenMs`
  (`/api/usage/requests` also `costUsd`); clients that ignore unknown fields need no change.

## 0.4.0: data retention

- **Data older than 365 days is removed the first night after the upgrade** (usage records, refused-key records, ended
  budget alerts, audit entries), with monthly usage totals kept. If your records-retention decision says otherwise, set
  *Settings > Data retention* right after deploying, or deploy with `Retention__Enabled=false` on the admin API until it is set.
- The migration `DataRetention` adds retention columns to `GatewaySettings`, the table `UsageMonthlyTotals` and the function
  `ume_retention_step`; it is additive. Re-run the role grants (the migration service does it) so `ume_admin` may execute the
  function: until then runs fail with `SQL state 42501`, shown on the Settings page.

## 0.4.0: client address restrictions

- The migration `KeyIpRangesTrustedProxies` adds `VirtualKeys.AllowedIpRanges` (`text[]`, default empty) and the table
  `GatewaySettings`. It is additive: the previous gateway and admin API keep working against the new schema. `app-roles.sql`
  grants the admin role writes on `GatewaySettings` and the Data API role `SELECT` on the new column; the migration service
  applies it on start. Run the migration before starting the new Data API, whose key inventory reads the column.
- `UME_FORWARDED_HEADERS` / `ASPNETCORE_FORWARDEDHEADERS_ENABLED` no longer affect the gateway. Behind a reverse proxy, list
  the proxy under *Settings > Trusted proxies* after the upgrade, or refused keys are recorded with the proxy's address and
  keys given an address restriction are refused. Check the proxy sends `X-Forwarded-For` (see the runbook).
- New 403 error code `address_not_allowed` for clients of keys with an address restriction.

## 0.3.0: shared routing rules

- **Rolling update: no**.
- The migration `RoutingRuleScopeSets` converts `RoutingRules.ScopeId` into the set `ScopeIds` (`uuid[]`), drops
  `ScopeId`, adds `IsTemplate` and inserts four disabled template rules. It is not expand/contract compatible: gateways and
  the admin API of the previous version cannot read the new schema, so drain traffic, migrate and roll out both together.
- Admin API clients: send `scopeIds: [...]` instead of `scopeId` when creating, updating or reassigning a rule, and
  `{ scope, ruleIds }` to reorder. Responses carry `scopeIds` and `owners` instead of `scopeId`/`scopeName`.

## Upgrade procedure

1. Review release/source changes, package/image advisories, licenses and SBOM; approve exact
   application/base digests. Keep Aspire SDK/hosting packages on the 13.6 release family.
2. Stop the exact AppHost before local builds. Run all test suites, production deployment
   checks and accessibility/manual regression appropriate to the change.
3. Back up the database, pepper, Data Protection key ring/certificate and nonsecret config.
   Rehearse restoring the backup in isolation; record RPO/RTO.
4. Review migrations for expand/contract compatibility and mark the release *Rolling update: yes/no* above. For a rolling
   update use `deploy/rolling-update.sh` or `deploy/kubernetes/deploy.sh`; otherwise drain gateway traffic and run the
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
