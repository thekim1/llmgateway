# Manual test checklist

Things built since the last manual test round that only a person with the real deployment (Foundry, Keycloak, Redis ACL,
real traffic) can confirm. Tick items off as you go; delete the file or empty the list when a round is done.

## Security hardening: low findings (branch `security/low-hardening`)

- [ ] **Redis down during a change:** stop Redis, revoke a key in the admin UI: it is revoked (no error), a notice says the
  gateways apply it when their cache refreshes, and after Redis is back and 30 s have passed the key is refused.
- [ ] **JSON logs:** `docker logs` of the gateway, admin API and Data API show one JSON object per line; the SIEM or log
  shipper still picks up `Ume.LlmGateway.Security` events.
- [ ] **API reference:** `https://<gateway>/scalar` and the Data API's `/scalar` render (browser console has no CSP
  errors); with `Gateway__PublishApiReference=false` both `/scalar` and `/openapi/v1.json` answer `404`.
- [ ] **Admin API reference:** as gateway-admin, `https://<admin>/scalar/` renders with no CSP errors in the browser console.
- [ ] **Provider refusal:** send a request a provider refuses (for example `"temperature": 5`): the error has the provider's
  message and the gateway's `request_id`, and nothing else from the provider.

## Security hardening H1–M4 (branch `security/h1-m4-hardening`)

- [ ] **Provider address and credential:** edit a provider with a stored credential and change only the path of the base
  URL: it saves and still works. Change the host instead without entering the credential: the form says *Enter the
  credential again…* and nothing is saved. Enter the credential with the new host: it saves.
- [ ] **Sign-out ends the session:** sign in, copy the `__Host-ume-admin` cookie (browser dev tools), sign out, then send a
  request with the copied cookie (for example `curl -b '__Host-ume-admin=…' https://<admin>/api/departments`): `401`.
- [ ] **Session limit:** with `Admin__MaxSessionHours=1`, sign in and wait for the expiry warning: it says sessions have a
  maximum length and offers no *Stay signed in*; after the hour the UI asks you to sign in. Remove the setting afterwards.
- [ ] **Everyone signed out once after the upgrade:** after deploying, existing admin sessions must sign in again (expected).
- [ ] **Redis ACL:** after the upgrade the admin API log has no `NOPERM` errors on sign-in (`ume:admin-session:*`).
- [ ] **PII in an Anthropic document:** with a *Block* key, send `/v1/messages` to a Claude deployment with a `document` block
  whose `source` is `{"type":"text","media_type":"text/plain","data":"… 19121212-1212 …"}`: `400 pii_blocked`.
- [ ] **PII look-alikes:** with a *Block* key, a personnummer written with full-width digits (`１９１２１２１２-１２１２`) or pasted
  with a no-break space is blocked; ordinary Swedish text without personal data still goes through.
- [ ] **Invalid-key throttle:** from one machine send 70 requests with a made-up key in a minute: the first 60 get `401`, the
  rest `429` with `Retry-After`. Meanwhile a real key used from the same machine just before keeps working. Behind the
  reverse proxy, check that another client is not affected (trusted proxies set).
- [ ] **Image scan in CI:** the *Verify POC* workflow's *Scan the service base image* step passes.

## Website and Getting started (uncommitted)

- [ ] **Website published:** after the push to `main`, the *Website* workflow succeeds and <https://thekim1.github.io/llmgateway/>
  opens. The menu, theme switch (light, dark, Lumen, remembered on reload), Getting started's *Try it locally* / *Deploy to
  production* switch, the copy buttons and the docs page filter work. On a phone the menu button opens the navigation.
- [ ] **Docs pages:** a few pages under *Docs* render with their images and tables; links between pages and to headings
  (for example *Runbook* from *Production setup*) land on the right place under `/llmgateway/`.
- [ ] **Admin UI:** *Getting started* is gone from the menu; `/getting-started` opens the *Catalogue*, which shows the
  gateway base URL (copy works) and a *Getting started guide* link that opens the website in a new tab.

## Already built (from earlier sessions)

- [ ] **Migration startup log:** a fresh database starts without the `42P01 DataProtectionKeys` error in the migration log (commit e9871ad).
- [ ] **Budget refunds with the Redis ACL:** no more `NOPERM ... 'decrby'` in the gateway log after a failed or cancelled request
  (redeploy with the updated `deploy/redis/users.acl.example` ACL) (commit d31f5e3).
- [ ] **Costs with 2 decimals:** spend and usage show öre (`12,34 kr`), not whole kronor (commit d31f5e3); budgets are in USD (see *Budgets in USD* below).

## Foundry and prices (uncommitted)

- [ ] **Foundry provider from the project URL:** create a provider of type *Azure AI Foundry* with the portal's project endpoint
  `https://<resource>.services.ai.azure.com/api/projects/<project>` and the *API key header*. The base URL stays as entered; a chat request to a deployment works (the gateway sends it to `…/openai/v1`).
- [ ] **Save and discover:** edit the seeded *ai-foundry* provider, paste your URL and key and click *Save and discover models* without saving first;
  it saves and the dialog lists your deployments.
- [ ] **Same model on two providers:** after the upgrade the gateway starts without `An item with the same key`. Both
  `DeepSeek-V4.1-Flash` models are listed (Providers > Models says *clients call <provider>/…*); a request to the plain name gets
  `400 model_ambiguous` naming both; `<foundry>/DeepSeek-V4.1-Flash` and `<ollama>/deepseek-v4.1-flash` each work and go only to that
  provider (break one provider: the request fails, no fallback). Then create a route `deepseek-v4.1-flash` with both targets:
  the plain name works and falls back in the route's order.
- [ ] **Discover models lists only deployments:** *Discover models* shows your deployed models (deployment names), not the whole catalogue, and no Claude.
- [ ] **Suggested prices:** GPT/o-series/embedding deployments show *Azure list price* with the meters it came from; PTU/batch and Grok show none.
  Compare one or two with the Azure pricing page.
- [ ] **Claude on Foundry:** a second provider of type *Anthropic* with the same project endpoint as base URL and the API key header;
  *Discover models* lists only the Claude deployments; a chat completion through the gateway to a Claude deployment works (streaming too).
- [ ] **GPT-5/6 reasoning:** a GPT-5 deployment gets the reasoning parameter profile; a request with `max_tokens` and `temperature` succeeds.
- [ ] **Responses API:** with *Responses* ticked on the Foundry provider, `POST /v1/responses` to a GPT-5 deployment works.
- [ ] **No price access:** if the admin API cannot reach `prices.azure.com`, discovery still works and logs one warning.

## Provider status and stability (uncommitted)

- [ ] **Status no longer sticks for 24 h:** the provider that showed *Degraded* while answering now shows *Healthy* (or *No recent traffic*) on
  Overview, with a reason on hover ("N of M requests failed at the provider in the last hour …"); Operations shows the same reason as text.
- [ ] **Client errors do not count:** send a few requests with an invalid parameter (provider answers 400) and cancel a streamed request;
  *Provider errors (24 h)* stays at 0 while *All failed (24 h)* rises, and the status stays *Healthy*.
- [ ] **Provider failures do count:** with at least 10 requests in the hour, make the provider fail (wrong port / stopped on-prem server with a
  single-target route); the status becomes *Degraded* or *Down* with the matching reason, and returns to *Healthy* about an hour later.
- [ ] **Provider stability page:** *Operations > Provider stability over time* (or the link under the Overview providers card) shows p95 latency
  and provider error rate per provider for 24 hours, 7 days and 30 days; hours and days match Swedish time; *View as table* lists the same values;
  the ranking puts the provider with the fewest provider errors first.

## Data API keys (uncommitted)

- [ ] **Create:** as gateway-admin open *Access → Data API access*, create a key with *Usage totals* only. The secret dialog shows `ume-dk-…` and a curl/PowerShell example with the Data API address (local: `https://localhost:7311`).
- [ ] **Use:** `curl -H "Authorization: Bearer <key>" "<data-api>/v1/usage/aggregate?from=<first of month>&to=<today>&granularity=day"` answers 200; `/v1/usage/records` answers 403; the same with `-H "X-Api-Key: <key>"` works.
- [ ] **Log and last use:** the Data API log has `data.read` with client `key:<name>`; after a request the list shows *Last used* (refresh the page).
- [ ] **Edit:** add *Usage records*; within 30 s `/v1/usage/records` answers 200.
- [ ] **Revoke:** revoke the key; within 30 s requests answer 401 and the Data API logs `data.auth.failed` with `key_revoked` and the prefix (never the key). Then *Delete* removes it; the audit log shows create, update, revoke and delete for `DataApiKey`.
- [ ] **Access:** a department-admin does not see *Data API access* in the menu and `/data-access` shows *No access*.
- [ ] **OIDC unchanged:** a token from the `ume-data-dev` client still works.
- [ ] **Production:** after deploying, the migration log shows the grants applied (no `42501` from the Data API when a key is used); set `UME_DATA_PUBLIC_URL`.

## Routing rules: shared rules, templates, switch (uncommitted)

- [ ] **Templates after migration:** *Routing > Routing rules* lists four global rules marked *Template*, all switched off, each with a short description.
- [ ] **Template blocked until its route exists:** switch on *Personal data to a local model*: it stays off and the message above the list names `local`.
  Create a route alias `local` (pointing at the on-prem model), switch it on again: it turns on and the *Template* mark is gone.
  With **Test a request** + *Personal data found* it now routes to `local`. Switch it off again afterwards.
- [ ] **Switch with the keyboard:** Tab to a rule's switch in the list, press Space: it toggles (screen reader / devtools show `role=switch`, `aria-checked`).
- [ ] **Shared rule:** *New rule* → scope *Key*, tick two keys in the list (use the filter box), save. Send a matching request with each key
  (`x-ume-rule` header set for both) and with a third key (no header).
- [ ] **New rule from template:** select a template → *New rule from template*; the form is prefilled; choose scope *Team* and two teams; save.
- [ ] **Deleting a shared team:** delete one of those teams in *Organisation*: no question for the shared rule (it keeps the other team);
  a rule only that team had still asks *Deactivate/Delete*.
- [ ] **Existing scoped rules survive the migration:** a key/team/department rule created before the upgrade still applies to the same owner.

## PII: personnummer and incidents (uncommitted)

- [ ] **Policy on:** set a test key's PII policy to *Redact* (the default *Off* scans nothing).
- [ ] **Personnummer formats:** chat with `900101-1234`, `900101 1234`, `19900101-1234` and `199001011234` (made-up numbers, wrong check digit),
  once with `content` as a string and once as a content-part array: the model receives `[PERSONNUMMER]` and the response has `x-ume-pii: redacted`.
  The valid test number `19121212-1212` is caught too.
- [ ] **Phone numbers stay phone numbers:** `Ring 0701234567` becomes `[TELEFON]`, not `[PERSONNUMMER]`.
- [ ] **Chat client:** the same in your chat UI (Open WebUI or similar) through the gateway, with streaming on.
- [ ] **PII incidents page:** *Monitor > PII incidents* lists those requests with time, key, team, category and action; the filters and tiles work;
  clicking a request id opens it on *Usage* with *PII found*.
- [ ] **Detected only:** switch the key to *Allow* and send an email address: it is listed as *Detected, sent on*.
- [ ] **Empty period:** a period without incidents shows the explanation that scanning is per key.
- [ ] **Department admin:** sees only incidents from their own departments.

## Client address restrictions and trusted proxies (uncommitted)

Locally the gateway sees `127.0.0.1` or `::1`; in WSL/Docker it may be a bridge address. A refused request's *Source address*
(Data API `security/auth-failures`, or `AuthFailures` in the database) shows what the gateway saw.

- [ ] **Key form:** edit a key, enter `203.0.113.0/24` under *Allowed client addresses*, save. The key details show *Client addresses* `203.0.113.0/24`.
- [ ] **Typo caught:** enter `10.20.1.5/16`: the form says *Did you mean 10.20.0.0/16?* and does not save; `example.se` is refused too.
- [ ] **Refused from elsewhere:** call the gateway with that key: `403` with `"code": "address_not_allowed"` and an English message. Within 10 s the
  refused-key record has reason `AddressNotAllowed` and your (truncated) address; `gateway.auth.failed` has `address_not_allowed`.
- [ ] **Allowed from here:** change the list to your own address (`127.0.0.1` and `::1` locally): the call answers 200 within 30 s
  (key cache) or at once after saving (the admin API clears the cache).
- [ ] **Empty list:** clear the field and save: the key works from anywhere again.
- [ ] **Rotation:** rotate a restricted key: the new key shows the same client addresses.
- [ ] **Department admin:** can set client addresses on own keys; does not see *Settings*, and `/api/settings/network` answers 403.
- [ ] **Settings card:** as gateway-admin open *Settings*: the *Trusted proxies* card explains the setting, shows the address the admin API
  saw, and *Before you change this* lists the caveats. `0.0.0.0/0` and `10.0.0.5/24` are refused before the confirmation.
- [ ] **Behind the proxy (test server with Nginx Proxy Manager):** with no trusted proxies, a key restricted to your workstation's address is
  refused (the gateway sees the proxy). The Settings card shows *X-Forwarded-For* and offers *Add <proxy address>*; add it, review and save.
  Within a few seconds the same request answers 200, and a refused request now records the workstation's address, not the proxy's.
- [ ] **Spoofing ignored:** from the workstation, call the gateway port directly (bypassing the proxy, if the firewall allows it) with
  `-H "X-Forwarded-For: <an allowed address>"`: still `403 address_not_allowed`.
- [ ] **Audit:** the audit log has `update Settings` with the before/after proxy lists, and `update VirtualKey` with `allowedIpRanges`.
- [ ] **Upgrade:** the migration `KeyIpRangesTrustedProxies` runs once; existing keys show *Any address*; the Data API `/v1/security/keys`
  includes `allowedIpRanges` (no `42501`). If the deployment set `UME_FORWARDED_HEADERS=true`, list the proxy in *Settings* instead.

## Data retention (uncommitted)

- [ ] **Card:** as gateway-admin open *Settings*: *Data retention* shows 365 / 365 days, *What is kept and what is removed*,
  "No usage has been removed yet", the next usage/audit cutoff and *Next run* tonight at 03:00.
- [ ] **Validation:** `30` or `4000` days is refused before the confirmation; ticking *Keep the audit log for ever* hides the audit field.
- [ ] **Run now on a fresh database:** *Run now* → "Running now…", then *Last run … 0 rows removed* within a few seconds; the admin
  API log has `Data retention run removed 0 rows`; the audit log has `run Settings`.
- [ ] **Old data goes, totals stay:** in a test database insert a usage record 200 days old for an existing key (or use an old
  restored database). Set usage to 90 days, *Run now*. The record is gone from *Usage > Requests*; *Usage* summary for the
  last 250 days still counts it, shows the note *Usage before … is only kept as monthly totals*, and by day shows a `yyyy-MM` row.
  The CSV export for that period includes it.
- [ ] **Yearly budget unchanged:** note a yearly budget's spent amount before the run above; it is the same after.
- [ ] **Audit kept for ever:** tick *Keep the audit log for ever*, save, *Run now*: audit entries older than 90 days remain.
- [ ] **Department admin:** does not see *Settings*; `/api/settings/retention` answers 403.
- [ ] **Production roles:** after deploying, *Run now* succeeds (no `SQL state 42501` on the card); in psql as `ume_admin`,
  `DELETE FROM "AuditLog" WHERE false;` fails with permission denied.
- [ ] **Data API:** `/v1/usage/aggregate?granularity=month` for a period before the oldest kept record returns the monthly totals.
- [ ] **Upgrade:** set the periods from the records-retention decision before the first night, or start with `Retention__Enabled=false`.

## Usage dashboard (uncommitted)

- [ ] **Upgrade:** the migration `RequestTimings` runs once; existing usage still shows on *Usage*.
- [ ] **Timings recorded:** send a non-streamed and a streamed chat request through the gateway. *Usage > Requests and costs*,
  open each: *Provider time* and *Gateway overhead* are shown (overhead a few ms); the streamed one also has *Time to first
  token*, below its latency. A request refused by a rate limit or budget shows neither.
- [ ] **Fallback counts as provider time:** with a route whose first target fails (e.g. a drained or wrong-URL provider),
  the request's provider time includes the failed attempt; overhead stays small.
- [ ] **Dashboard against real traffic:** *Usage* opens on *Dashboard* with *7 days*. The totals match the *Requests and costs*
  summary for the same days (requests, tokens; cost now in US$). Hover a bar: the tooltip lists the series and a total.
- [ ] **Time ranges:** *24 hours* shows hourly bars with times on the axis; *30 days* and *90 days* daily; *Custom* with dates
  and *Apply*. Days follow Swedish midnight.
- [ ] **Filters:** pick a department, model, *Outcome: Provider error* and *Endpoint*; each reloads at once and *Clear filters*
  resets them. A department viewer sees only their department and no *Provider* filter.
- [ ] **Views:** switch a card between bar, line and table; the table lists every period.
- [ ] **Themes and size:** check Light, Dark and Lumen, and a phone-width window (cards stack, no sideways scrolling).
- [ ] **Speed:** the 30- and 90-day views of the production database load within a few seconds (see docs/performance.md).
- [ ] **Link from PII incidents:** a request link from *PII incidents* opens the *Requests and costs* tab with the drawer.
- [ ] **OpenTelemetry:** the Aspire dashboard (or your collector) has `ume.gateway.overhead` and `ume.gateway.time_to_first_token`.

## Upgrade 0.4.0 to 0.4.1: MCP gateway removed (uncommitted)

- [ ] **Upgrade a 0.4.0 database with MCP traffic:** on a copy of a 0.4.0 instance that has MCP calls (or after calling a
  tool through `/mcp` on 0.4.0), deploy 0.4.1. The migration service log says `Database schema is at migration
  20261014144400_RemoveMcpGateway`, and `\dt "Mcp*"` in psql lists no tables.
- [ ] **Usage still opens:** *Usage > Requests and costs* and *PII incidents* load for a range that covered the MCP calls
  (no error); the MCP calls are gone, LLM requests from the same period are still listed.
- [ ] **Redis ACL:** after redeploying and restarting Redis, the gateway and admin API logs have no `NOPERM`.

## Streaming chunk gaps (uncommitted)

- [ ] **Real OpenAI/Azure stream:** stream a chat completion through a real OpenAI or Azure provider; tokens arrive
  continuously and *Usage* shows the exact token count (usage still read from the last chunk).
- [ ] **Client asked for usage:** with `stream_options.include_usage: true` the client still gets the usage chunk; without it,
  it does not.
- [ ] **Metrics:** after a few streamed requests the Aspire dashboard (or your collector) has `ume.gateway.stream.max_upstream_gap`
  and `ume.gateway.stream.max_write` per provider; `max_write` stays at a few ms.
- [ ] **Stall log:** a stream with a pause of ≥ 1 s (e.g. a reasoning model, or Azure with the default content filter) logs
  `stream from <provider> stalled` with its request id, and `ume.gateway.stream.stalls` counts it.
- [ ] **Azure filter:** if an Azure deployment stalls often, switch it to the asynchronous filter and compare the stall rate.

## Fake LLM timing (uncommitted)

- [ ] **Local timing:** set `FakeLlm:Latency:FirstTokenMs=400` and `TokenIntervalMs=25` in the AppHost user secrets and
  restart; a streamed chat in the admin UI playground (or curl) starts after about 0.4 s and then streams word by word.
- [ ] **Off by default:** without the section, the fake LLM still answers instantly.

## Whole release

- [ ] **Portainer upgrade to 0.3.0:** new stack file pasted, image variables at 0.3.0, Redis restarted after the redeploy;
  the gateway log has no `NOPERM ... decrby` after a cancelled or failed request.
- [ ] **Data API in Portainer:** `COMPOSE_PROFILES=data` starts `dataapi` (healthy); port 10443 reachable; a Data API key from
  the admin UI works against `https://<host>:10443/v1/usage/aggregate?...`.
- [ ] **Upgrade the deployed instance:** the migration `SharedRulesDataKeysPii` runs once; gateway and admin API are deployed together
  (the routing rule API changed from `scopeId` to `scopeIds`, see docs/upgrade-guide.md).

## Usage writer with COPY (uncommitted)

- [ ] **Usage rows with the production role:** after deploying, send a few chat requests (one streamed, one with PII on a
  redacting key, one routed by a rule) and check *Monitor > Usage*: each shows up with model, provider, tokens, cost, latency,
  PII categories and rule name. The gateway log has no `Failed to write … usage records` (the `ume_gateway` role needs only its existing INSERT grant).
- [ ] **Last used:** *Keys* shows *last used* updated for each key you used (within a minute).
- [ ] **Budget alert:** a key with a small budget still raises its 50/80/100 % alert once.

## Per-request log line only for unsuccessful requests (uncommitted)

- [ ] **Gateway log:** after deploying, a successful chat request leaves no "Request … status 200 in … ms" line in the gateway log
  (it is in *Monitor > Usage*); a request with an unknown model or an exhausted budget still logs "Request … status 4xx".
- [ ] **Debug switch:** with `Logging__LogLevel__Ume.LlmGateway.Gateway.Pipeline.GatewayRequestHandler=Debug` on the gateway, successful requests are logged again.

## Updates without downtime and Kubernetes (uncommitted)

- [ ] **Single instance unchanged:** an existing `compose.prod.yaml` stack upgraded to this version starts and works as
  before (no second slots, no new `.env` values needed).
- [ ] **Compose rolling setup:** `init-deployment.sh --proxy --rolling` on a test host writes `proxy/nginx.conf` with two
  servers per upstream and `UME_GATEWAY_B_PORT` / `UME_ADMIN_B_PORT` in `.env`; the stack starts with both compose files and
  shows `gateway`, `gateway-b`, `adminapi` and `adminapi-b` healthy.
- [ ] **Rolling update with your real proxy:** behind the proxy you use in production (nginx, Nginx Proxy Manager or F5),
  keep a streamed chat request and a stream of short requests running and run `./deploy/rolling-update.sh --force`; no
  request fails, the long stream finishes, and you stay signed in to the admin UI throughout.
- [ ] **F5 / HAProxy monitor:** with a `/health/ready` monitor, a stopping slot is marked down before it stops answering
  (set `UME_DRAIN_SECONDS` above interval × failures).
- [ ] **Kubernetes:** on a test cluster, follow docs/kubernetes.md with the example overlay (your Ingress controller,
  your IdP): sign in, send a chat request through the Ingress, then change an image tag and run `deploy.sh` while requests
  run; none fail.
- [ ] **Own Postgres:** with an external Postgres (CloudNativePG or managed) and the roles from `init.sql`, the migrations
  Job succeeds and the gateway works.

## Request logging switch (uncommitted)

- [ ] **Card text:** *Settings > Request logging* shows *Off* and explains that every request is still in the usage records; the
  *Monitor › Usage* link works; *What it costs* opens.
- [ ] **24 hours:** turn on *For 24 hours*; within a few seconds successful requests appear in the gateway log
  ("Request … status 200 …") on every gateway instance; the card shows *On until* tomorrow's time; the audit log has the change.
- [ ] **No time limit / off:** switch to *No time limit* (card: *On, no time limit*), then *Turn off…*; successful requests leave the log again.
- [ ] **Department admin:** a department admin does not see the card's settings page (or gets 403 from `/api/settings/request-log`).


## Budgets in USD, English messages (uncommitted)

- [ ] **Upgrade conversion:** on a copy of a database with SEK budgets (e.g. 10 000 kr per month) and a USD rate of 9,50, run the
  migration: *Budgets & alerts* shows the budget as about 1 052,63 US$ and the same percentage used as before; old alerts
  show their amounts in US$.
- [ ] **Budget forms in USD:** *Budgets & alerts > New budget*, the key details' budgets and *New team* ask for the limit in USD;
  lists, the department page and the overview's alert banner show US$ amounts.
- [ ] **Enforcement:** give a key a budget of 0,01 USD and send a few chat requests: they stop with `402 budget_exceeded` and a
  message such as *The key's budget (USD 0.01 per month) is used up*.
- [ ] **Headers:** a successful chat request answers `x-ume-cost-usd` and `x-ume-budget-remaining-usd`, and still
  `x-ume-cost-sek` / `x-ume-budget-remaining-sek` (the USD amounts × the exchange rate).
- [ ] **Webhook:** with `Gateway:AlertWebhookUrl` pointing at a request catcher, cross an alert threshold: the body has
  `spentUsd`, `limitUsd`, `spentSek` and `limitSek`.
- [ ] **Data API:** `GET /v1/catalog/budgets` has `limitUsd` and `limitSek` for each budget.
- [ ] **English messages:** a wrong API key, a missing `model`, an unknown model and a PII block all answer English messages;
  in the admin UI, deleting a team that has keys and saving a duplicate budget show English messages.
- [ ] **Department admin scope:** signed in as a department admin, *Keys*, *Budgets & alerts* and *Usage* show only your own
  departments' data, and changes appear in the audit log under your name.

## Version and new releases (uncommitted)

- [ ] **Released image:** on an install of an older release (e.g. 0.6.0 while 0.6.1 is out), *Settings > Version* shows *Running
  0.6.0*, *Update available*, *Version 0.6.1 is available* and a *Release notes* link that opens the GitHub release in a new tab;
  **Settings** in the side menu has a **New** badge (a dot with the menu collapsed).
- [ ] **Up to date:** on the latest release the card shows *Up to date* and the menu has no badge.
- [ ] **Local build:** under Aspire the card shows *0.0.0-local* with *Development build*, and no badge.
- [ ] **Switched off:** with `Admin__CheckForReleases=false` the card shows the version and *Not checked*; the admin API log has no
  GitHub warning.
- [ ] **No internet:** with outbound HTTPS blocked, the card shows *Could not reach GitHub*, the rest of Settings works and the
  admin API logs one warning (not one per page load).
- [ ] **Department admin:** a department admin gets 403 from `/api/settings/version` and sees no badge.

