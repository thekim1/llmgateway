# Changelog

## Unreleased

## 0.8.3 - 2026-10-26

Agents that call tools through Chat Completions work on gpt-5.6 and GPT-6 (gpt-6-astra, -sol, -luna, gpt-6.1) again:
the gateway bridges those requests to the Responses API. No configuration, no schema change. Rolling update: yes.

**Tool calls work on gpt-5.6 and GPT-6 through Chat Completions.** On these models a Chat Completions request with
function tools failed with *"Function tools with reasoning_effort are not supported … use /v1/responses"*, whatever the
client sent: they reason by default, and gpt-6-astra and gpt-6.1-sol also reject `reasoning_effort: "none"`. The gateway
now serves such a request through the provider's Responses API and returns the answer, or the stream, in Chat Completions
form, as LiteLLM and Bifrost do, so agents keep working with reasoning on and nothing needs configuring. Known model names
(gpt-5.6 and later, GPT-6 and later) are bridged at once; a deployment with a name of its own is bridged when the provider
first answers with that error, and remembered until restart. Requests without tools and requests with
`reasoning_effort: "none"` are unchanged. See *Tool calls on gpt-5.6 and GPT-6* in `docs/providers-and-models.md`.
Rolling update: yes.

## 0.8.2 - 2026-10-25

Provider speed on Overview, Health & operations and Provider stability is now the provider's own time to first token,
and failed requests can be listed from every error count. One admin API rename (`p50LatencyMs`/`p95LatencyMs` become
`p50FirstTokenMs`/`p95FirstTokenMs`; see the 0.8.2 section of `docs/upgrade-guide.md`). Rolling update: yes.

**Provider speed is the provider's time to first token.** The p50/p95 per provider on Overview, Health & operations and
Provider stability was the whole request's latency, so long answers and failed attempts at other providers made a fast
provider look slow. The gateway now records, for each streamed answer, the time from sending the request to the provider
that answered until its first event (`ProviderFirstTokenMs`; Data API and request details `providerFirstTokenMs`, metric
`ume.gateway.provider.time_to_first_token`), and the provider figures use it. Answers that are not streamed have no
comparable figure and are not counted. The admin API fields are renamed `p50FirstTokenMs`/`p95FirstTokenMs` (see the
upgrade guide). Rolling update: yes.

**Failed requests are easy to find.** *Usage › Requests and costs* has an *Outcome* filter (*All errors* or one outcome),
plus model and provider filters, and shows the error code under each outcome. Each *Errors* count in the summary lists
that row's failed requests, and the Usage dashboard's Requests card and Overview's error figures link to them.
`GET /api/usage/requests` takes `errors=true`, `model` and `provider`.

## 0.8.1 - 2026-10-24

Costs in USD only: SEK and the exchange rate are removed from every interface (breaking for anything that reads the
SEK headers, webhook fields or Data API fields; see the 0.8.1 section of `docs/upgrade-guide.md`). The admin UI works on
iPhone again (menu, search), and the routing rule test says when a model does not exist. Rolling update: yes.

**Costs in USD only; SEK and the exchange rate removed.** Prices were already in USD, and budgets and the admin UI
already counted and showed USD; the exchange rate under Settings only fed SEK copies of the same amounts. It is gone,
with every SEK output: the `x-ume-cost-sek` and `x-ume-budget-remaining-sek` response headers, `spentSek`/`limitSek` in
budget alert webhooks, `costSek` in the Data API's usage records and aggregates, `limitSek` on its budgets and its
`/v1/catalog/exchange-rates`, the `ume.gateway.cost` metric (SEK; `ume.gateway.cost.usd` stays), the admin API's
`/api/settings/exchange-rate`, the gateway's `exchange-rate` readiness check and the `Gateway:FallbackSekPerUsd`
setting. Breaking for anything that reads those; see the [upgrade guide](docs/upgrade-guide.md). Rolling update: yes.
The database keeps the old `CostSek` columns (no longer written) and `ExchangeRates` table for one more release.

**Admin UI on iPhone.** The phone menu opened as a strip showing only its title in Safari on iOS; it now opens at full
height. Search no longer zooms the page in when it opens (iOS zooms into fields with text smaller than 16px, so fields
use 16px there), and its results fit above the on-screen keyboard, so the last ones can be reached.

**Fixes.** *Routing rules › Test a request* showed a model name that does not exist as a disabled model; it now says
*Does not exist*. The *Request logging* status on Settings is shorter (*Off: problem requests only*), so it fits on a
320px screen. The browser tests (`tests/e2e`) follow the current admin UI again: the *Requests* tab on Usage, the
*New rule* button next to *New rule from template*, English error messages, target-size checks limited to the open
dialog, and a new sign-in when an earlier test signed the cached session out.

## 0.8.0 - 2026-10-24

A clearer admin UI with a menu grouped by task, search into pages and a working phone layout; costs in US$ throughout
the admin UI; a regrouped, phone-friendly website; and fewer npm packages with limits on what they can do. Rolling update:
yes (no migrations). One format change: the usage CSV export has `kostnad USD` instead of `kostnad SEK` (see the 0.8.0
section of `docs/upgrade-guide.md`).

**Admin UI costs in USD.** Overview (spend this month, daily spend, spend by department), Usage (totals, grouped tables,
request details) and the Catalogue prices showed Swedish kronor; they now show US$, like budgets. The usage CSV export
has `kostnad USD` instead of `kostnad SEK`, and the admin API's usage summaries and catalogue report USD. The Data API,
gateway response headers and budget webhooks still include SEK. See the [upgrade guide](docs/upgrade-guide.md).
The daily spend chart on Overview shows all 30 days, also those without spend (with one day of usage, that day's bar
filled the whole card).

**Website: docs menu by task, and a working phone layout.** The documentation menu is grouped by what you came to do:
Get started (now with Identity provider and roles), Build with the API, Set up the gateway, Run it, Security and
compliance, Performance and Reference. On phones and tablets the page dropdown is replaced by a docs menu that shows where
you are, plus an *On this page* list; the menu closes with Esc or a tap outside, and on narrow phones the theme switch
moves into it so the header fits at 320px. Fixed sideways scrolling on narrow phones (the SDK sample on the home page and
long code in headings). Admin UI menu paths in the docs follow the new menu (*Models & routing › Routing rules*). The
manual test checklist is removed; manual testing is tracked elsewhere.

**Fewer npm packages, and limits on what they can do.** The admin UI no longer uses Reka UI: dialogs, drawers, search
and the phone menu use the browser's `<dialog>` element, which takes 17 packages out of the code that runs in the
browser (and the unused `axe-core` out of the admin UI's dev tools). Each npm project has an `.npmrc` that turns off
install scripts, skips versions published in the last 7 days when packages are added or updated, and refuses
dependencies from git; `npm ci` and the lockfiles are unchanged. `undici` (used by the unit tests) updated for
GHSA-4qv8-2p9j-2685. See [npm packages](docs/security-and-compliance.md#npm-packages).

**Admin UI: clearer menu, search into pages, and a working phone layout.** The side menu is grouped by task: Monitor,
Access, Models & routing, Cost & compliance and System (Catalogue moved to Models & routing, PII incidents to Cost &
compliance, Settings to System; *Operations* is now *Health & operations*, as on the page). Search (Ctrl K) also finds
cards and tabs inside pages and everyday words (*retention*, *circuit*, *department*), works with the arrow keys and opens
full screen on phones. On phones a **Menu** tab opens every page, the theme, the session and **Sign out**, and the end of
a page is no longer hidden behind the bottom bar. Fixes: the session timer lost its hours ("h 34 min left"), the "Skip to
main content" link showed over the header on phones, and the daily spend and provider stability charts had no height
(Tailwind classes using spacing steps that did not exist produced no CSS). The time labels under narrow
charts (the Usage cards) no longer run together, and the Overview circuit alert says *Open Health & operations*.
Screenshots on the website and in `docs/images` are retaken with the new menu. See [Admin UI](docs/admin-ui.md#finding-your-way).

## 0.7.1 - 2026-10-23

Fix release: admin sign-in failed with a 500 on deployments with the shipped Redis ACL. Rolling update: yes (no
migrations). Redeploy the Redis ACL (see the 0.7.1 section of `docs/upgrade-guide.md`).

**Fix: admin sign-in failed with a 500 (RedisServerException).** Admin sessions in Redis (0.6.4) are written with a
time to live, which StackExchange.Redis sends as `SETEX`/`PSETEX`. The shipped ACL only allowed `set`, so every sign-in
failed with NOPERM after the identity provider returned. `deploy/redis/users.acl.example` (and the generated Portainer
stack) now allow `setex` and `psetex`; the forced circuit-breaker open (`StringSetAsync` with a duration) needed them too.
Redeploy the stack (Portainer) or rerun `init-deployment.sh` and restart Redis (Compose) to regenerate `users.acl`.

## 0.7.0 - 2026-10-23

An on-prem content guard, token quotas for sharing on-prem capacity, no usage lost while the database is down, and
gateway improvements from a comparison with agentgateway (Retry-After cooldown, least-loaded balancing, retries, sliding
rate limits with a concurrency cap, request policies, prompt caching, cache-write prices). Rolling update: yes (five
additive migrations). Read the 0.7.0 section of `docs/upgrade-guide.md` first: the Redis ACL must be redeployed for the
usage journal, per-minute limits slide and a failed last candidate is retried once, and token quotas and
`/v1/messages/count_tokens` should wait until the rollout is finished.

**Content guard (on-prem).** A per-key check for prompt injection and harmful content (*Keys > Content guard*: Off,
Record, Block; admin API `contentGuardPolicy`). Rules in the gateway catch common prompt-injection phrasings in English
and Swedish and your own patterns (`Gateway:ContentGuard:BlockedPatterns`); an optional guard model (Llama Guard or IBM
Granite Guardian) on your own on-prem server checks meaning (`Gateway:ContentGuard:Model`, `Format`), and complete
answers too with `CheckAnswers`. The gateway never uses a guard model that is not on an on-prem provider. Record keeps the
categories on the usage record (*Usage*, *Content guard*), adds `x-ume-guard: flagged` and logs `gateway.content.flagged`;
Block answers `content_blocked`. Keys with the guard off do no extra work. See [docs/content-guard.md](docs/content-guard.md).
Migration `ContentGuard`.

**No usage lost when the database is down.** When Postgres refuses a batch of usage records, the gateway keeps it in a
usage journal in Redis and writes it when Postgres is back, instead of holding it in memory: the queue no longer fills,
so requests are not held up and records are no longer dropped after 10 s. Another gateway instance takes over the batches
of one that stopped. `Gateway:UsageJournal:Mode`: `Spill` (default; no work while Postgres is healthy), `Always` (every
batch is journaled first, so a crash loses nothing) or `Off`; `MaxRecords` (1 000 000) caps it. The operations status adds
`journaledBatches`. The Redis ACL needs `zrangebyscore` (read-only). Measured: no change in throughput (`audit`, 64 clients).

**Token quotas.** A budget can count tokens instead of USD (*Budgets > New budget > Counts: Tokens*), optionally only for
requests served by providers of one residency, such as *On-prem models only*: the way to share on-prem capacity, whose
models cost nothing, between teams and keys. Same alerts, same 402 when used up, a fallback to another residency is not
charged. Reserved in the same Redis call as cost budgets (no extra round trip). Admin API `unit`, `limitTokens`,
`residency`, `spentTokens`; alerts and the webhook carry token amounts. Migration `TokenBudgets`.

Improvements taken from a comparison with agentgateway ([docs/agentgateway-comparison.md](docs/agentgateway-comparison.md)).

**Retry-After cooldown.** A provider answer with `Retry-After` or `retry-after-ms` (a 429 from Azure OpenAI or OpenAI)
puts that model deployment on cooldown for that long, shared by all gateway instances through Redis: it goes last, like
an open circuit, so requests stop paying a wasted round trip to a deployment that is out of quota. It no longer counts
toward the provider's circuit breaker, so a quota on one deployment no longer closes the provider's other deployments.
Capped by `CircuitBreaker:MaxRetryAfter` (default 5 minutes; `00:00:00` turns it off). No extra Redis round trip.

**Least-loaded balancing (opt-in).** `Gateway:LoadBalancing=LeastLoaded` picks the first of several targets with the
same priority by power of two choices over each instance's moving averages of latency and success and its requests in
flight, so a slow, failing or saturated deployment gets less traffic. The default stays `Weighted`. See
[routing rules](docs/routing-rules.md#evaluation-order).

**Retries.** The last candidate of a request is retried once, after a short jittered delay, when it fails with a
connection error or 502/503/504 (not a timeout, a 429 or a client error): a route with one target no longer fails on a
network blip. `Gateway:Retry:MaxRetries` (1; 0 turns it off) and `Gateway:Retry:BaseDelayMs` (250). `x-ume-fallbacks` and
the usage record count every extra attempt; the `all_providers_failed` message counts attempts.

**Smoother rate limits and a concurrency cap.** Requests and tokens per minute are counted over a sliding minute (the
previous minute weighted by how much of it the last 60 seconds cover), so a client can no longer send twice its limit
across a minute boundary; `Retry-After` says when one more fits; refused requests no longer count. Keys get *Requests at
once* (`maxConcurrentRequests`): requests in progress across all instances, so one busy key cannot saturate a shared
on-prem server. Same single Redis round trip. Migration `KeyConcurrencyLimit`.

**Request policies per team and key.** A cap on output tokens, a system prompt placed before the application's own,
default parameters and forced parameters (admin UI: *Request policy* on the team and key forms; admin API `requestPolicy`).
A key's values win over its team's. Applied after the PII policy and the routing rules, before the budget estimate (which
the cap tightens). Migration `RequestPolicies`.

**Token counting and context windows.** `POST /v1/messages/count_tokens` is passed to Anthropic providers, free (the
Anthropic SDKs offer it; it answered 404 before). A model whose context window the prompt clearly exceeds is skipped, so a larger
fallback model takes long prompts; with none left the answer is 400 `context_length_exceeded` before anything is sent.

**Prompt caching.** OpenAI-format requests translated for Claude get Anthropic cache markers on the tools and the system
prompt (`Gateway:PromptCaching`: `Static` by default, `Conversation` or `Off`). The header `x-ume-session` (or OpenAI's
`prompt_cache_key`) keeps a conversation on the same target, so vLLM and Ollama prefix caches are reused.

**GenAI metrics (opt-in).** With `Gateway:GenAiMetrics=true` the gateway also records the OpenTelemetry GenAI
semantic-convention metrics (`gen_ai.client.token.usage`, `gen_ai.client.operation.duration`,
`gen_ai.server.request.duration`, `gen_ai.server.time_to_first_token`) for standard GenAI dashboards. Off by default, so
the request path does no extra work. Always: `ume.gateway.cost.usd`; `ume.gateway.cost` (SEK) stays until SEK is removed.

**Flaky tests outside the pull request pipeline.** Tests that depend on timing or on races between the shared fixtures'
parallel test classes are marked `[Trait(TestCategory.Name, TestCategory.Flaky)]`. `dotnet test` still runs every test
locally, and the weekly *Flake hunt* runs them too; *Verify POC* runs with `--filter-not-trait "Category=Flaky"`. Four
tests are marked, each with the reason (see *Tests that do not flake* in the local development guide).

**Faster Claude streams through the OpenAI API.** The translated stream writes its chunks with `Utf8JsonWriter`: 683 → 291
µs and 1 270 → 379 KB per 300-chunk stream.

**Cache-write prices.** A model price can set *Cache write* (USD per 1M tokens) for providers that charge for writing
the prompt cache. Anthropic `cache_creation_input_tokens` were billed at the input price; with a cache-write price
(1.25x input for Claude) they are billed correctly. Admin API and config export: `cacheWriteInputPerMillionUsd`
(optional); Data API prices feed: the same field. Migration `CacheWritePrice` adds a nullable column.

**One verify run per pull request.** *Verify POC* ran twice for every push to a branch with a pull request (on the push
and on the pull request). It now runs on pull requests, on pushes to `main`, on release tags and by hand (Actions tab,
*Run workflow*), and a newer push cancels the run still in progress for the same pull request.

## 0.6.4 - 2026-10-22

Security hardening from an internal security assessment (findings H1–M4, L2, L4, L5, A3, A5, A6, LG1, LG2, LG4 and LG5),
and a live audio fix. Rolling update: yes (two additive migrations). Read the 0.6.4 section of `docs/upgrade-guide.md`
first: provider tools and storage are off for every key until an admin allows them, console logs are JSON, everyone is
signed out once, and existing Compose installations should renew the internal CA.

**Provider credentials stay with their address.** Changing a provider's base URL to another scheme, host or port now
requires entering the credential again (`400`, field `credential`); the stored credential is never sent to a new address.
Another path on the same host keeps it. The same applies to configuration import. Before, an edit of the address alone
sent the stored API key to whatever host was entered.

**Runtime image 10.0.13.** The service images are built on `aspnet:10.0.13-noble-chiseled-extra` (two critical and five
high ASP.NET Core CVEs in 10.0.12). CI now scans the pinned base image and fails on fixable HIGH/CRITICAL findings.

**PII guard reads more of the request.** Anthropic plain-text and custom-content documents (`document` blocks with a `source`
of type `text` or `content`) are scanned like messages; before, a personnummer in such a document passed a key with policy
*Block*. The request's `user` field and a message's `name` are scanned too (tool and function names are not). Text is
compared in a canonical form, so full-width and other non-ASCII digits, no-break spaces, zero-width and other invisible
characters and Unicode dashes no longer hide a personnummer; a redacted value is written in that form.

**Admin sessions end when you sign out.** Sessions are kept on the server (Redis, encrypted), the cookie only names one:
signing out ends the session for every copy of the cookie. A session lasts at most `Admin:MaxSessionHours` (12) from
sign-in, however often it is extended; after that the user signs in at the identity provider again, which also picks up
role changes. `GET /bff/user` and `POST /bff/session/extend` return `sessionLimitAt`; extending at the limit answers `409`.
Everyone is signed out once by the upgrade.

**Floods of made-up keys are throttled.** After `Gateway:AuthFailuresPerMinute` (60) failed authentications in a minute
from one client address (IPv6 per /64), the gateway stops looking keys up for that address until the minute ends: keys
it already has cached keep working, others get `429 rate_limited` with `Retry-After`. `0` turns it off.

**Repeated JSON properties are refused.** A request body such as `{"model":"a","model":"b"}` gets `400 invalid_request`
(it was a `500` with no usage record). On live audio such an event is answered with an `error` event, not passed on.

**Provider refusals are trimmed.** When a provider refuses a request (4xx), the client gets only its `message`, `type`,
`code` and `param` in the client API's error shape with the gateway's `request_id`; inner errors, deployment and
organisation names stay out.

**A saved change is not reported as failed when Redis is down.** The admin API used to answer `500` after a change was
saved if it could not tell the gateways; now it retries once, logs a warning and answers with
`Ume-Change-Propagation: delayed`, and the admin UI says the gateways apply it when their cache refreshes.

**JSON console logs.** Outside development the services log JSON (set `Logging__Console__FormatterName=simple` for plain
text), and model names and Data API paths are logged with control characters replaced, so a client cannot forge log lines.

**API reference CSP and switch.** `/scalar` on the gateway, Data API and admin API gets a strict Content-Security-Policy
(scripts only with a per-request nonce). `Gateway:PublishApiReference` and `DataApi:PublishApiReference` (default on) turn
the public reference and OpenAPI document off.

**Database roles hold only what they use.** `app-roles.sql` now revokes and grants again in one transaction on every
migration-service start, naming each table: the gateway role cannot read the audit log or Data API key hashes or add Data
Protection keys (the gateway no longer generates them; the admin API does), and the admin role can change the settings
row only column by column, without `UsageRetainedFrom`. A test compares the effective grants, so a new table is readable
by no service until it is granted on purpose.

**Conformant internal CA.** The generated CA has `keyUsage` (certificate and CRL signing), and service certificates have
`keyUsage` and `authorityKeyIdentifier`, so strict TLS clients (Python 3.13, newer Go and Java) accept the chain.
`init-deployment.sh --renew-ca` replaces an older CA; the Portainer bootstrap does it on its own. The setup now tells you
to move `ca.key` off the host.

**Provider tools and storage need the key's permission.** Two new key settings, both off for every key: *Allow provider
tools* (web search, file search, code interpreter, MCP, web fetch and other tools the provider runs itself; otherwise only
function tools, else `400 tool_not_allowed`) and *Allow provider storage* (`store: true`, Responses `background` and
`conversation`; otherwise `400 storage_not_allowed`, and Responses API requests are sent with `store: false`). Live audio
refuses `mcp` tools the same way. Both are in the key form and detail, the admin API (`allowProviderTools`,
`allowProviderStorage`), the audit log and the Data API key inventory, and are copied on rotation; refusals are
`gateway.request.refused` events. Migration `ProviderFeaturePolicy`.

**Admin activity is attributable.** Audit entries now record the user's name, roles and departments at the time, the client
address behind the trusted proxies (`Admin:AuditSourceAddress`, `Full` by default), a session id given at sign-in (never the
cookie), the request's trace id and the user agent; the audit page shows them and the Data API audit feed passes them on
(migration `AuditActorContext`). Configuration and usage exports are audit entries. New security events: `admin.signin`,
`admin.signout`, `admin.session.extended`, `admin.access.denied` (at most once a minute per user, with a count) and
`admin.data.read` (audit log, request list, PII incidents, Data API keys), with suggested SIEM alerts in
`docs/security-events.md`.

**Live audio sessions stopped by the gateway close with the right code.** When the budget ran out, the provider's answer to
the gateway's close could reach the client first: the client got close code 1000 instead of 1008 and the session could be
recorded as a normal close instead of `BudgetExceeded`. The gateway's own close and outcome now always win.

**Website:** the icons on the "Control over AI" cards are outlined like the rest of the site, without a fill.

## 0.6.3 - 2026-10-20

**`:latest` image tags.** Releases also tag the service images `:latest` on ghcr.io (pre-releases do not), so a Portainer
stack can follow the newest release without editing image variables; see *Portainer* in the runbook.

## 0.6.2 - 2026-10-20

**Version and new releases in Settings.** *Settings > Version* shows the running version and the newest GitHub release with a
link to its release notes; when a newer one exists, **Settings** in the menu gets a **New** badge. The admin API checks
`api.github.com` at most every 6 hours (`GET /api/settings/version`); turn it off with `Admin__CheckForReleases=false` where
the admin API has no internet access.

## 0.6.0 - 2026-10-18

**Budgets in USD.** Budget limits, spend counters and reservations are now in USD, the currency provider prices are in,
so what a budget allows no longer moves with the exchange rate. The migration `BudgetsInUsd` converts existing limits at
the current rate (stored alerts at the rate when they were raised). The admin UI shows budgets and alerts in USD; the
admin API uses `limitUsd` / `spentUsd`; the gateway adds `x-ume-cost-usd` and `x-ume-budget-remaining-usd`, the alert
webhook `spentUsd` / `limitUsd` and the Data API budgets feed `limitUsd`. The SEK headers and fields stay, converted at
the exchange rate, until SEK is removed. Not a rolling update: see the [upgrade guide](docs/upgrade-guide.md).

**Error messages in English.** Every message the gateway, admin API and Data API answer with is in English (the admin
UI no longer marks server messages as Swedish). Error codes are unchanged. In the gateway every refusal is one
`GatewayError` (status, code, message) from a single catalogue in `GatewayErrors.cs`.

**Admin API internals.** `AdminContext` carries the signed-in user of the request, so department scoping and audit
entries no longer depend on every endpoint passing the user along; role, claim and policy names are constants
(`AdminRoles`, `AdminClaims`, `AdminPolicies`), and the authentication setup is `AddAdminAuthentication`.

**Gateway internals.** The `/v1` routes are mapped from the endpoint table (`GatewayRoutes`); audio and image uploads share
one multipart reader (`MultipartFormReader`; a cancelled audio upload now returns its pooled buffer); provider adapters read
complete answers through one `ReadCompleteAsync` and no longer dispose their timeouts by hand. Provider benchmarks are
unchanged (same allocations; chat JSON 3.1 µs, embeddings 448 µs, 300-chunk stream 223 µs).

**Updates without downtime (opt-in).** Every service now fails `/health/ready` as soon as it is told to stop and, for the
new setting `Shutdown__DrainSeconds` (default 0), keeps answering with `Connection: close` before it closes its listener,
so a load balancer stops sending it requests and a proxy retires its pooled connections without losing a POST. For Docker
Compose, the overlay `deploy/compose.rolling.yaml` runs a second gateway and admin API on their own host ports, and
`deploy/rolling-update.sh` migrates and then replaces one slot at a time; `init-deployment.sh --rolling` writes the nginx
configuration with both slots. A single-instance `compose.prod.yaml` installation is unchanged; Portainer is not supported.
See [Updates without downtime](docs/zero-downtime-updates.md).

**Kubernetes.** Kustomize manifests in `deploy/kubernetes`: gateway and admin API with rolling updates, probes and
PodDisruptionBudgets, the migrations Job, optional in-cluster Postgres and Redis and the optional Data API, all under the
restricted Pod Security Standard. `create-secrets.sh` turns what `init-deployment.sh --no-chown` generates into secrets, and
`deploy.sh` runs the migration before rolling out. The release bundle includes them, pinned to the released images. See
[Kubernetes](docs/kubernetes.md).

**Tested.** The new workflow *Deploy end to end* builds the images and replaces every instance under load, with Compose
behind nginx and on a kind cluster, and fails on a single lost request (0 of about 800 000 locally; without the drain one
POST was lost). The upgrade guide now marks every release *Rolling update: yes/no*.

## 0.5.0 - 2026-10-15

**Website.** The project now has a website on GitHub Pages (<https://thekim1.github.io/llmgateway/>), built from `site/`
after the claude.ai/design *Website* design: a product page, Getting started (local and production), Benchmarks and every
documentation page, rendered from the Markdown in `docs/` so the repository and the site never disagree. The new workflow
`.github/workflows/pages.yml` builds it on pull requests (a link to a missing page, file or heading fails the build) and
publishes it from `main` when the site, the docs, the changelog or the admin UI's design tokens change. The admin UI
pictures on the site are taken from the real UI in all three themes by `tests/e2e/docs/website.spec.ts`.

The README is now a short overview; its sections moved to their own pages in `docs/` (introduction, local development,
production setup, personal data, attached files, speech to text, image generation, live audio, providers and models, admin
UI, client addresses, data retention and logging, security events). The routing examples were already in
`docs/routing-rules.md`.

**Getting started moved from the admin UI to the website.** The admin UI's *Getting started* page is gone (old links go to
the *Catalogue*). The Catalogue now shows the gateway base URL with a copy button and links to the website's guide, which has
SDK examples, the error codes and the terms. The per-alias code snippets (`utils/snippets.ts`) went with the page.

**Fix: a key change could be missed right after it was published.** The gateway shares one database lookup per key
among concurrent requests. A lookup that had started before a revocation or other key change could still be handed to
a request that arrived just after the change, which was then served with the old key state (once; it was never cached).
Lookups are now only shared within the same cache generation. This also made the test
`Expired_key_is_served_while_it_refreshes_and_revocation_drops_it` fail now and then.

**Request logging switch.** *Settings > Request logging* lets a gateway admin log every request again, for 24 hours (it
switches itself off) or without a time limit. It applies to all gateway instances within seconds, without a restart, and is
audited. The card explains what is recorded with the switch off (every request is a usage record) and what turning it on
costs. Admin API: `GET`/`PUT /api/settings/request-log`. Migration `RequestLogSwitch` adds two columns to `GatewaySettings`.
With the switch off, the gateway's speed is unchanged: it reads the setting from its cached configuration.

**Successful requests are no longer logged one by one.** The per-request line "Request … key … model … status … in … ms"
is now written at Debug for successful requests; refused, failed and cancelled requests still log it at Information.
Every request is still in the usage records and the metrics. Writing that line for every request cost 9–16 % of peak
throughput and about 13 % of the gateway's CPU per request. To get the old behaviour, set
`Logging__LogLevel__Ume.LlmGateway.Gateway.Pipeline.GatewayRequestHandler=Debug` (see the runbook). The compare benchmark
takes `GATEWAY_LOG_LEVEL` to measure the gateway at another log level.

**Faster usage writer.** The gateway writes usage records with one Postgres binary `COPY` per batch instead of an
EF `INSERT` per row, in batches of up to 5 000 (was 500), and sets `VirtualKeys.LastUsedAt` for every key of a batch in one
statement instead of one per key. Records, budget alerts and `LastUsedAt` are written in one transaction. A batch that is
retried after its commit answer was lost skips the records already stored by request id, so it cannot write them twice
(rows were matched by row id before, which a lost commit leaves unset). No migration or new database grant is needed.
Measurements: [docs/performance.md](docs/performance.md#usage-writer-copy-2026-10-14).

## 0.4.1 - 2026-10-14

**MCP gateway removed.** The MCP gateway that 0.4.0 added (`/mcp`, `/v1/mcp/tools`, hosted `mcp` tools on `/v1/responses`,
the MCP connector on `/v1/messages`, `/api/mcp/servers`, MCP grants on keys and the *MCP servers* page) is reverted, with
its dependency `ModelContextProtocol`. The new migration `RemoveMcpGateway` drops the tables `McpServers`, `McpTools` and
`VirtualKeyMcpGrants`, the columns `UsageRecords.McpServerId`, `McpMethod`, `McpTool`, `ParentRequestId` and
`VirtualKeys.McpCallsPerMinute`, and the history row of migration `McpGateway`. It also deletes the usage records,
monthly totals and refused-key records of the endpoints `Mcp` and `McpToolCall`, which 0.4.1 can no longer read (the
request list and PII incidents failed on them); the cost of MCP tool calls drops out of usage reports and of budget
counters rebuilt from Postgres. On a database that never ran 0.4.0 it does nothing. The Redis ACL no longer allows the channel `ume:mcp:events` (redeploy the stack or run
`init-deployment.sh`, then restart Redis); leftover `ume:mcp:*` and `ume:rl:mcp:*` keys are unused and can be deleted.

**Dependencies updated.** All NuGet packages are on their latest stable versions (.NET 10.0.13, OpenTelemetry 1.19,
Microsoft.Extensions.Http.Resilience and ServiceDiscovery 10.11, Scalar 2.18, xUnit v3 4.0.2, Testcontainers 4.16,
WireMock.Net 2.19). This fixes the build, which failed NuGet's audit on known vulnerabilities in the transitive
`System.Security.Cryptography.Xml` and `System.Security.Cryptography.Pkcs` 10.0.12.

**Fake LLM with real-provider timing, and a streaming latency benchmark.** The fake LLM has an optional `FakeLlm:Latency` section (time to first token, pace per word, jitter, answer length, occasional stalls; all off by default, passed through from the AppHost user secrets), and its chat chunks carry `"usage":null` like OpenAI's when `include_usage` is set. `benchmarks/compare/latency.sh` measures time to first token, chunk gaps and whole answers per platform with a new stream probe (`stream-probe` in the benchmark project). `FakeLlm:Latency:PrefillMsPer1kInputTokens` makes time to first token grow with the prompt, and `PROBE_PAYLOAD=document` sends a ten-page document in a five-message conversation (about 10 000 tokens; eneo's seeded model now allows 128 000 input tokens). The comparison seed now retries while Docker's port forward comes up. Results of a full run against eneo: [docs/benchmark-2026-10-13.md](docs/benchmark-2026-10-13.md).

**Streaming: faster relay of real OpenAI/Azure chunks, no compressed SSE, chunk-gap metrics** ([docs/streaming-findings.md](docs/streaming-findings.md)). OpenAI and Azure send `"usage":null` in every chunk when `include_usage` is set (the gateway always sets it), which sent every chunk down the full-JSON-parse path. Null usage now takes the fast path: 300 chunks 672 → 258 µs and 1 580 → 433 KB allocated. Streamed provider calls no longer send `Accept-Encoding`, so a provider or proxy cannot hold tokens back in compression blocks; a stream that arrives compressed anyway is still decoded. New OpenTelemetry metrics per stream: `ume.gateway.stream.max_upstream_gap`, `ume.gateway.stream.max_write` (ms) and `ume.gateway.stream.stalls` (waits ≥ 1 s), tagged by provider and endpoint, plus an Information log line for streams that stalled. New benchmark row with the real OpenAI wire format.

## 0.4.0 - 2026-10-13

**Usage dashboard.** *Monitor > Usage* opens on a dashboard tab with nine cards: cost in USD stacked by model, requests by
outcome (success, provider error, refused, cancelled), tokens (input, cached input, output), the provider cache hit rate,
requests by model, latency (avg, P50, P95, P99), gateway overhead, time to first token and throughput (output tokens per
second of provider time). Each card has a bar, line and table view, a text summary and a hover tooltip; the page has
time-range presets (24 hours to 90 days, or custom dates) and filters for department, team, key, model, provider, outcome
and endpoint. The earlier summary and request list moved to the *Requests and costs* tab (`/usage?tab=requests`), and the
request drawer shows the cost in USD, provider time, gateway overhead and time to first token. New endpoint
`GET /api/usage/metrics` (hourly or daily buckets, department-scoped, aggregated in SQL). The gateway now records provider
time (every attempt, failed fallbacks included, and the streamed answer) and time to first token for streamed answers:
migration `RequestTimings` (`UsageRecords.UpstreamMs`, `TimeToFirstTokenMs`), new fields `upstreamMs` and
`timeToFirstTokenMs` in the Data API usage feed and `/api/usage/requests`, and the OpenTelemetry histograms
`ume.gateway.overhead` and `ume.gateway.time_to_first_token`. No measurable effect on the request path (docs/performance.md).

**Images.** `POST /v1/images/generations` (JSON) and `POST /v1/images/edits` (`multipart/form-data`: one or more pictures in
`image` or `image[]`, an optional `mask`) in OpenAI format, for gpt-image-1, DALL·E, FLUX and other OpenAI-compatible image
models. New provider capability `Images`, model kind `Image` (discovery recognises `gpt-image*`, `dall-e*`, `flux*`, `imagen*`
and `stable-diffusion*`), endpoints `ImageGenerations` and `ImageEdits` (routing names `image_generations`, `image_edits`),
and the price field `perImageUsd` next to the token prices (admin API, config export/import, Data API prices feed, admin UI
price form, price history and model list). The cost is the number of pictures in the answer × `perImageUsd` plus reported
tokens × token prices, and the budget reserves the worst case for `n` pictures first. Edits are read into pooled in-memory
buffers like audio (never a temporary file) up to `Gateway:MaxImageRequestBodyBytes` (50 MB for all pictures, at most 16) and
sent again from memory on fallback; the pictures count as image attachments for the key's *Attached files* setting. `stream`
and `partial_images` are refused with `400 invalid_request`. The dev seed has `ume/image`; FakeLlm answers both endpoints;
the admin UI shows image examples for image aliases. Migration `ImageGeneration` (`PerImageUsd` on `ModelPrices`, default 0).
Usage records have no picture count yet: reports show the cost and tokens of an image request, not how many pictures.

**Data retention.** Old data is removed every night so the database stays bounded. New card *Settings > Data retention*
(gateway-admin): **usage data** (usage records, refused-key records, budget alerts of periods that ended over a year ago)
and the **audit log** each have a period of 90–3 650 days, both **365 by default**; the audit log can be kept for ever.
Usage goes in whole Europe/Stockholm months after each month's totals per key, team, department, model, provider and
endpoint are saved in the new table `UsageMonthlyTotals`. Budget spend, the usage summary (`monthlyTotalsBefore`; by day,
removed months are one `yyyy-MM` row), the CSV export and the Data API monthly aggregate read the totals before the oldest
kept record (`GatewaySettings.UsageRetainedFrom`) and records after it, so yearly budgets and monthly reports stay exact.
The admin API runs it at 03:00 local time (`Retention:Enabled`, `RunAt`, `BatchSize`) and on *Run now*
(`POST /api/settings/retention/run`); the card shows the last run, rows removed, errors and the next run. Removal is the
database function `ume_retention_step` (owner's rights, 90-day floor, batches): `ume_admin` gets `EXECUTE` on it, not
`DELETE`, so the audit log stays append-only for recent entries (ADR 016). Settings changes and manual runs are audited.
Migration `DataRetention`; `app-roles.sql` grants the function to `ume_admin` and `UsageMonthlyTotals` plus
`GatewaySettings.UsageRetainedFrom` to `ume_data`. New automated test of the production database roles (`DatabaseRoleTests`).
**On upgrade, data older than 365 days is removed at the first night**; set the periods first if you need longer.

**Keys restricted to client addresses, and trusted proxies.** A key can be limited to the client addresses it may be used
from: single IPs or CIDR networks, IPv4 and IPv6, at most 100 (`VirtualKey.AllowedIpRanges`, admin API `allowedIpRanges`,
admin UI key form *Allowed client addresses*, shown in the key details, copied on rotation, in the Data API key inventory).
Empty means any address. Requests from elsewhere get `403 address_not_allowed`, recorded as the refused-key reason
`AddressNotAllowed` (`address_not_allowed` in `gateway.auth.failed`). Behind a reverse proxy, a gateway admin lists the proxy
under the new **Settings > Trusted proxies** card (`GET/PUT /api/settings/network`, gateway-admin only, audited as
`Settings`); the gateway then takes the client address from `X-Forwarded-For`, read from the right, on connections from those
addresses only, and applies changes within seconds. The card shows the address the admin API saw for the page request (usually
the proxy) with a button to add it, and explains the caveats; `/0` networks are refused. The address is resolved before
authentication, so refused-credential records now carry the client's address instead of the proxy's. Both checks run in
memory with no database or Redis call: 0.4 ns when unused, 51 ns through a trusted proxy, 6 ns for a key with five networks
(`ClientAddressBenchmarks`); the pipeline benchmarks are unchanged within noise. Migration `KeyIpRangesTrustedProxies` (column
`VirtualKeys.AllowedIpRanges`, default empty; table `GatewaySettings`); `app-roles.sql` grants `ume_admin` writes on
`GatewaySettings` and `ume_data` `SELECT` on the new column, applied by the migration service on the next start.
`deploy/proxy/nginx.conf.example` now sets `X-Forwarded-For`.

**Changed: `UME_FORWARDED_HEADERS` is gone.** The gateway now ignores `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which made it
believe `X-Forwarded-For` from any caller and would have let a client claim an allowed address. The Compose files no longer
pass `UME_FORWARDED_HEADERS`. If you set it, list your proxy under *Settings > Trusted proxies* instead; until then refused
keys are recorded with the proxy's address.

**Admin UI:** the Settings page no longer says theme options are in the top bar (they are in the sidebar).

**Deploy: the Redis ACL follows upgrades.** `users.acl` is derived from the Redis password and `deploy/redis/users.acl.example`,
so the Portainer stack's bootstrap and `init-deployment.sh` now rewrite it on every start/run instead of only creating it.
An existing deployment gets new Redis permissions (such as `+decr +decrby` from 0.3.0) by redeploying the stack (or running
`init-deployment.sh` again) and restarting Redis; no hand edit of the secrets volume. Only the stack file and scripts
changed, not the images.

**Deploy: image defaults follow the version.** The fallback image names in `compose.prod.yaml` and the Portainer stack (`${DATAAPI_IMAGE:-ume-llm-gateway/dataapi:…}` and the others; used only when the variable is unset, and replaced by the pinned images in a release bundle) still said 0.2.0. They now say 0.3.0, and CI fails when they differ from `<Version>` in `Directory.Build.props`.

## 0.3.0 - 2026-10-11

**Fix: the same model from two providers.** Two models whose names differ only in case (`DeepSeek-V4.1-Flash` from Foundry, `deepseek-v4.1-flash` from Ollama) stopped the gateway from loading its catalogue ("An item with the same key has already been added"). Model names are now unique per provider (migration `ModelNamesPerProvider`), so several providers can offer a model of the same name. Clients use the *public name*: the plain name while one provider has it, `<provider>/<model>` when several do (`<provider>/<model>` always works). A plain name several providers share is refused with `400 model_ambiguous` listing the choices; the gateway never picks a provider or falls back to another one by itself: only a route an admin creates does. `GET /v1/models`, `/api/catalog` and `/api/models` (`publicName`) show public names; routing rules and config import accept public and `<provider>/<model>` names; discovery adds models under their own name and marks names other providers also offer. A key that allows the plain name may use each provider's model of that name.

**Fix:** *Discover models* in the provider form used the saved provider, so a URL or key typed but not yet saved was ignored (a seeded placeholder such as `example-swc.services.ai.azure.com` gave "No such host is known"). With unsaved changes the button now reads *Save and discover models*: it saves first and opens discovery only if the save succeeds.

**Provider status and stability.** A provider's status (Overview and Operations) is now computed by the admin API from the **last hour**, not the last 24 hours, and counts only failures the provider is responsible for: outcome `ProviderError` with a 5xx status (upstream 5xx, timeouts, broken streams), not client errors the provider rejected (4xx), cancellations (499), the gateway's own `internal_error` or requests the gateway refused (budget, rate limit, PII). With fewer than 10 requests in the hour only the circuit counts; with none the status is *No recent traffic*. Degraded from 5 % provider errors, Down from 50 %; an open circuit or a disabled provider wins. `GET /api/ops/health` returns `status`, `statusReason` (e.g. "4 of 40 requests failed at the provider in the last hour (10 %).", shown as text on Operations and as tooltip/screen-reader text on Overview), `recentRequests`, `recentProviderErrors` and `providerErrorRate24h`; the 24-hour figures are unchanged. Before, every unsuccessful request (client errors and cancellations included) over a rolling 24 hours counted, so one burst kept a provider *Degraded* for up to a day while it answered normally. **New:** `GET /api/ops/providers/stats?from&to&bucket=hour|day` (gateway-admin) gives requests, provider errors, error rate, fallbacks and p50/p95 latency per provider per hour (UTC hours, at most 7 days) or per day (Europe/Stockholm days, at most 90 days), aggregated in SQL, plus a ranking for the period (availability = 1 − provider error rate, then p95). The admin UI has a new page **Operations > Provider stability** (linked from the Overview providers card and Operations) with a 24 h / 7 days / 30 days selector, p95 latency and provider error rate charts per provider (text summary and table view for each) and the ranking, most stable first. **Gateway:** a request on which every provider failed (502/504 `all_providers_failed`) is now recorded on the last provider tried (still nothing charged); before it had no provider, so a provider that was down was invisible in the provider statistics and status. No schema change.

**PII detection:** personnummer and samordningsnummer are now caught even with a wrong check digit when the format is unambiguous: with a separator (`YYMMDD-NNNN`, `YYMMDD+NNNN`, or a single space) or with a four-digit year (`YYYYMMDD-NNNN`, `YYYYMMDDNNNN`, century 18/19/20). Made-up test numbers such as `900101-1234` or `199001011234` used to pass unredacted. The calendar date is still checked, and a bare 10-digit number (`YYMMDDNNNN`) still needs a valid Luhn digit so mobile numbers like `0701234567` are not taken for personnummer. Note that a key's PII policy defaults to *Off* (no scanning).

**PII incidents:** new admin UI page *Monitor > PII incidents* and endpoint `GET /api/usage/pii-incidents`: every request in which PII was found (including policy *Allow*: detected and sent on), with time, key, team, department, endpoint, model, categories and counts, action and outcome; filters for period, department, team, key, category and action; counts per category and per action; rows link to the request on *Usage* (`/usage?request=<id>` opens it), whose detail drawer now shows the PII found. New partial index `IX_UsageRecords_Timestamp_Pii` on `UsageRecords.Timestamp` where `PiiCategories` is set (migration `PiiIncidentsIndex`).

**Data API keys:** gateway admins can give integrations access to the read-only Data API from the admin UI (*Access → Data API access*): create a key with a name, the permissions it may use (`usage.aggregate`, `usage.detail`, `security.read`, `catalog.read`) and an optional expiry; the key (`ume-dk-…`) is shown once with a curl and PowerShell example, and can be edited, revoked and (once revoked or expired) deleted. Admin API `/api/data-access` and `/api/data-access/keys` (gateway-admin only, audited as `DataApiKey`). The Data API accepts the key as `Authorization: Bearer` or `X-Api-Key` next to OAuth access tokens; the caller is `key:<name>` in `data.read` events and the rate limit; use of a revoked or expired key is the new security event `data.auth.failed` (3002). Only the SHA-256 of a key is stored. New settings: Data API `DataApi:AllowKeys` (true) and `DataApi:KeyCacheSeconds` (30; revocation delay); `DataApi:Authority` is now optional while keys are allowed. Admin API `DataApi:PublicUrl` (Compose `UME_DATA_PUBLIC_URL`) for the example. New table `DataApiKeys` (migration `DataApiKeys`); `app-roles.sql` grants `ume_admin` writes on it and `ume_data` `SELECT` on its hash columns and `UPDATE ("LastUsedAt")`, applied by the migration service on the next start.

**Routing rules: shared rules, templates and an on/off switch.** A key, team or department rule now applies to a **set** of owners (`RoutingRule.ScopeIds`, Postgres `uuid[]`, replacing `ScopeId`), so one rule can serve several keys without copying it; the gateway indexes enabled rules by owner when it builds the rule set. Names are unique among global rules and among rules of one scope that share an owner; reordering is per scope. Deleting a team or department removes it from shared rules and only asks (409 `routing_rules_scoped`) about rules it is the sole owner of; *Deactivate* leaves them with no owners. Four **template** rules are seeded, disabled, by the migration `RoutingRuleScopeSets` (`IsTemplate`): personal data to a local model, header opt-in to the local model, large prompts to a long-context model, budget nearly used to a cheaper model; they target placeholder aliases (`local`, `long-context`, `cheap`). A disabled rule may now name a model or route that does not exist yet; switching it on is refused until it exists. New `PUT /api/routing-rules/{id}/enabled`; `RuleView` gains `isTemplate`, `scopeIds`, `owners`, `missingModels` (drops `scopeId`, `scopeName`). **Breaking API change:** `scopeId` is replaced by `scopeIds` in create/update/reassign, and reorder takes only `{ scope, ruleIds }`. Templates are not exported. **Admin UI:** the rule list shows the title, a short description and an accessible on/off switch per rule; the form has *Title*, *Short description*, an *Enabled* switch, a filterable multi-select of keys/teams/departments and *Start from a template*; templates are badged and offer *New rule from template*.

**Fix:** the migration service no longer logs `42P01: relation "DataProtectionKeys" does not exist` on a fresh database. It skips ASP.NET's startup key ring load (`KeyRingStartup.LoadKeyRingOnFirstUse`), which ran before the migration created the table; the key ring now loads on first use, and the gateway or admin API creates the first key. Entity Framework's own failed `__EFMigrationsHistory` query on a fresh database is still logged.

**Fix:** the Redis ACL now allows `DECR`/`DECRBY`. When a request cost less than its budget reservation the gateway refunds the difference, which StackExchange.Redis sends as `DECRBY`; the ACL refused it (`NOPERM User gateway has no permissions to run the 'decrby' command`), so budget counters kept the full reservation (budgets ran out sooner than real spend) and budget alerts were skipped; usage records were still written. **Existing deployments:** `users.acl` is generated once, so add `+decr +decrby` to the `user gateway` line in the secrets volume (`redis/users.acl`) and restart Redis.

**Admin UI:** every cost, spend and budget amount is shown with two decimals (`0,37 kr`, not `0 kr`), so small and per-request costs are visible. The catalogue also shows `—` instead of a broken character for a model without capabilities.

**Azure AI Foundry:** *Discover models* lists the project's deployments (`/api/projects/<project>/deployments?api-version=v1`), by deployment name, instead of `/openai/v1/models`, which lists every model the resource could deploy. A Foundry provider keeps the portal's project endpoint (`…/api/projects/<project>`) as its base URL, so discovery knows the project; the gateway sends requests to the resource's `…/openai/v1` (an Anthropic provider on Foundry: `…/anthropic/v1`), derived when it loads the catalogue. Before, requests were sent under the project path and failed. Without a project in the base URL discovery falls back to the default project `_project`, which not every resource has; a 404 now says to enter the project endpoint.

**Foundry prices and Claude:** discovered Foundry deployments get a suggested USD price from Azure's public retail price list (`Admin:AzurePriceRegion`, default `swedencentral`), matched on model, version and deployment type and only when the meters agree; the dialog marks it *Azure list price* with the meters used, and shows audio token prices for speech models. Claude deployments are listed for an Anthropic provider on the same resource (`…services.ai.azure.com/anthropic/v1`) and no longer for the Foundry provider, whose OpenAI API does not serve them. GPT-6 and later are discovered with the reasoning parameter profile, like GPT-5.

## 0.2.0 - 2026-10-10

GitHub releases. Pushing a tag `vX.Y.Z` (equal to `<Version>` in `Directory.Build.props`) runs the new `release.yml`: the verify workflow, then the four service images pushed to `ghcr.io/<owner>/<repo>/{gateway,adminapi,migrations,dataapi}:X.Y.Z`, then a deploy bundle (`deploy/build-release-bundle.sh`) whose `compose.prod.yaml` and `compose.portainer.yaml` default to those images pinned by digest, booted with `init-deployment.sh` as a smoke test before the release is published with the bundle, `compose.portainer.yaml`, the SPDX SBOM and `SHA256SUMS`. **Fix:** `compose.portainer.yaml` was invalid (`configs.dataapi.environment must be a string`): the Data API service sat under `configs:` instead of `services:`. CI now validates both production Compose files, including the `data` profile. `build-portainer-stack.sh` strips CR from inlined files, and `.gitattributes` keeps shell scripts and the files mounted into containers (`redis.conf`, `pg_hba.conf`, SQL, YAML, the Redis ACL) at LF on Windows checkouts. The SBOM version is read from `Directory.Build.props` instead of being fixed at 0.1.0.

Data plane cleanup. **Accounting:** every request that passes authentication now ends in exactly one usage record. An unexpected exception after the request was accepted releases the budget reservation, is recorded (outcome `ProviderError`, error `internal_error`, nothing charged for HTTP) and answers 500 with the new error code `internal_error` in the gateway's JSON error format; a client abort is recorded as 499. A shared store that times out during accounting no longer drops the usage record; a record is dropped (and logged) only if the usage queue stays full for 10 s. **Exchange rate:** new option `Gateway:FallbackSekPerUsd` (default 10, 0.01–1000) replaces the silent 10 SEK fallback; it is used only while no USD exchange rate is in effect, each catalogue load that uses it logs a warning, and the new readiness check `exchange-rate` reports Degraded (so does the gateway in the admin operations status). **Circuit breaker:** only transient failures (408, 409, 429, 5xx, timeouts, connection errors, invalid JSON) count; upstream 401/403/404, `https_required`, `credential_unavailable` and `endpoint_not_supported` still fall back to the next provider but no longer open the circuit. **Behaviour:** `/v1/models` hides models that no endpoint of their kind can reach on their provider; a realtime input transcription model must be on an available provider and within the key's provider and residency allow-lists; the realtime budget message names the exhausted budget like the HTTP 402. **Alert webhooks** run on their own queue (1 000): one POST per alert, 5 s timeout, no retries; when the queue is full the alert is stored but not sent; webhooks no longer delay usage writing. Providers receive `User-Agent: Ume-LlmGateway/<informational version>`, and Kestrel's `MaxRequestBodySize` comes from the validated `Gateway:MaxRequestBodyBytes`. **Internals:** `GatewayEndpoints` (Domain) is the single table of endpoint semantics (routing name, paths, capability, model kinds, audio/realtime/streaming flags); `GatewayRequestHandler` is split into partials with stages shared by HTTP and realtime; `ProviderAuth`/`ProviderTransport`; `FailureKind` replaces `Retryable`; decrypted provider credentials are cached per catalogue snapshot; `RedisKeys` lists every Redis key (all under `ume:`, which the ACL in `deploy/redis/users.acl.example` relies on); `ISpendLedger.TryReserveAsync` removed; shared `GatewayQueries` (Infrastructure) for exchange rate, key rotation lineage and budget spend, used by the gateway and the admin API.

Cross-cutting cleanup. **Model discovery** uses the gateway's provider auth and URL rules: in Production a non-https provider address is refused with 400 "Leverantörens adress måste använda https.", an invalid base URL is 400 (was 500), a query string on the base URL is kept (also for Ollama's `/api/show`), and discovery makes one attempt with its own 20 s timeout (no retries from the standard resilience handler). The gateway uses the shared ServiceDefaults web defaults (headers unchanged and now pinned by tests). The pinned .NET base image and container user are set once in `Directory.Build.targets` for projects with `<UmeServiceContainer>true</UmeServiceContainer>`. Tests share `tests/Ume.LlmGateway.TestKit`, and every test fixture migrates the database before starting its host (fixes an intermittent `42P01 DataProtectionKeys` failure). Culture, logging and unused-using analyzers are enabled; projects generate XML documentation files so IDE0005 runs on build.

Control plane cleanup. **Errors:** unhandled errors answer 500 with "Ett internt fel inträffade." in both the admin API and the Data API (the Data API said "Internal error."); a malformed request body in the admin API is 400. The Data API now sends `X-Frame-Options: DENY`, and the security headers are reapplied on exception-handler responses. **CSV:** the admin usage export quotes cells only when needed (it quoted every text cell); a cell starting with a tab or line break, or with `=`, `+`, `-` or `@` after leading spaces, is prefixed with `'`, numbers are left alone; the Data API's CSV feeds use the same rule (`CsvCell`, Domain). **Audit:** `details` are dedicated snapshots `{ before, after }` (camelCase, enum names, no `deploymentCount`, `createdAt`, `currentPrice`, `providerName`, key status or `lastUsedAt`); rotation records `{ previousKey, key }`, config import `{ created, updated, skipped }`; no more `invalidate` audit rows except the manual key-cache flush. Config import refuses changing a routed model's kind with the same message as the API ("Modelltypen kan inte ändras när modellen används i en rutt."). `/api/ops/health` lists providers sorted by name; `GET /api/settings/exchange-rate` returns an empty 200 when no rate is set. **Configuration:** validated on start: `Oidc:Authority` required, `Admin:RequestsPerMinute` (1–10000), `Gateway:BaseUrl`/`Gateway:OperationsUrl` absolute http(s) URLs (the catalogue falls back to `https://localhost:5140`, the operations probe to `BaseUrl`), and the migration service's `Seed:*`; `AdminApi/appsettings.Development.json` removed. **Structure:** the admin API has a service layer (`RoutingRuleService`, `ProviderCatalogService`, `BudgetAdminService`, `ConfigTransferService`, `OwnerScopeResolver`) with typed contracts; budget and alert lists no longer query per row, and provider 24 h statistics are aggregated in SQL. Shared ServiceDefaults helpers: `WebDefaults` (`ExitIfHealthProbeAsync`, `AddUmeWebDefaults`, `UseUmeWebDefaults`, `UseUmeSecurityHeaders`) and `ApiExceptionHandler` with `ApiFaultException` (replaces `AdminFaultException`); `OidcOptions` replaces `OidcClaimOptions`.

Live audio over WebSocket (OpenAI Realtime protocol): `GET /v1/realtime?model=` for live transcription (speech-to-text aliases) and realtime sessions (new `ModelKind.Realtime`), and `GET /v1/realtime/translations?model=` for live interpreting (new `ModelKind.SpeechTranslation`, e.g. gpt-realtime-translate). New `ProviderCapabilities.Realtime` and `GatewayEndpoint.Realtime`/`RealtimeTranslations`. The usual pipeline runs once at connect (key, attachment policy, alias, rate limit, routing rules `endpoint == "realtime"`, residency, budget reservation) and the provider connection is opened, with fallback, before the client's handshake is accepted, so refusals are plain HTTP errors. The upstream URL is the provider's base URL plus `/realtime[/translations]` with `model=<upstream>`: OpenAI, Azure OpenAI and Azure AI Foundry (`/openai/v1`), Foundry Voice Live, and on-prem servers with the same protocol (vLLM, Speaches). Events are relayed as received; the gateway only reads the event type, measures appended audio without decoding it, rewrites `model` in `session.update` (an input transcription model must be a gateway speech-to-text model on the same provider), runs the key's PII policy on text events (block, redact, or refuse what may only go on-prem), refuses binary frames, and reads usage from `response.done` and transcription events. Budgets are reserved in blocks (`Gateway:Realtime:ReserveMinutes`, 5) and checked every `BudgetCheckSeconds` (30); an exhausted budget, the session limit (`MaxSessionMinutes`, 120) or shutdown ends the session with an `error` event. At most `MaxSessionsPerKey` (20) open sessions per key across instances (Redis sorted set; the gateway ACL gains `zadd zrem zcard zremrangebyscore`, and without them the limit is skipped with a warning). One usage record per session (`latencyMs` = session length, `audioSeconds`). Metrics `ume.gateway.realtime.sessions`, `ume.gateway.realtime.session.duration` and `ume.gateway.audio`; realtime sessions no longer count towards `ume.gateway.request.duration`. **Billing:** new `ModelPrice.AudioInputPerMillionUsd` and `AudioOutputPerMillionUsd`: audio tokens (reported in `*_token(s)_details.audio_tokens`) are billed at these prices when set, which also corrects gpt-4o-transcribe (audio input 6 USD vs. text 2.50 USD per million); added to the not yet released migration `AudioTranscription`, the admin API, config export/import, the Data API catalog and the UI price form. **Providers:** base URLs may carry query parameters such as `api-version` (credential-like parameters are refused); they are kept after the appended path for HTTP, WebSocket and model discovery. **Admin:** discovery classifies `*realtime-translate*` and `*realtime*` models instead of hiding them; the UI has the new kinds, capability and audio token prices; *Getting started* shows Python, .NET and Node.js WebSocket examples for live aliases; the rule tester has the new endpoints. FakeLlm serves both WebSocket endpoints and the dev seed adds `ume/live-transcribe`, `ume/realtime` and `ume/interpret`. The nginx example passes the WebSocket upgrade. **Fix:** five error messages in model discovery had corrupted characters ("Leverant�ren").

Speech to text: `POST /v1/audio/transcriptions` and `POST /v1/audio/translations` (OpenAI format, `multipart/form-data`). The upload is read into one pooled in-memory buffer (never a temporary file) up to `Gateway:MaxAudioRequestBodyBytes` (26 MB); its text fields go through the normal pipeline, so model aliases, fallback (the file is sent again from memory), residency, routing rules (`endpoint == "audio_transcriptions"`), rate limits, budgets, the PII policy on `prompt` and the attachment policy (audio is refused for *Images only* and *Text only* keys) all apply. Answers pass through in the provider's format (`json`, `text`, `srt`, `vtt`, `verbose_json`, or SSE for gpt-4o-transcribe with `stream=true`). New `ProviderCapabilities.AudioTranscriptions`, `ModelKind.Transcription` and `GatewayEndpoint.AudioTranscriptions`/`AudioTranslations`. **Billing:** new `ModelPrice.AudioPerMinuteUsd` (Whisper) next to token prices (gpt-4o-transcribe); duration is taken from `usage.seconds` or `verbose_json`'s `duration`, otherwise from the file header (`AudioDuration`: WAV and FLAC exact, MP3 from the bitrate, 32 kbps assumed otherwise), and reservations use the same estimate. Migration `AudioTranscription` adds `UsageRecords.AudioSeconds` and `ModelPrices.AudioPerMinuteUsd` (both default 0). The Data API adds `audioSeconds` to usage records and aggregates (last column in CSV) and `audioPerMinuteUsd` to catalog prices. **Admin:** model discovery now adds `whisper*` and `*-transcribe` models as speech to text instead of hiding them; the UI has the new kind and capability, a per-minute price field, and speech-to-text code examples on *Getting started*. FakeLlm answers transcriptions (`fake-whisper`, `fake-transcribe`) and the dev seed adds the alias `ume/transcribe`. The nginx example and runbook turn off request buffering so recordings never land in proxy temp files.

Attached files: video and large images. **Fix:** video parts (`video_url`, used by vLLM and other OpenAI-compatible servers, and `input_video`) were not recognised, so keys set to *Images only* or *Text only* sent them on; they are now the attachment kind `Video` and refused like documents and audio. Chat Completions requests to Claude models with video return 400 `unsupported_content` with a video-specific message. **Fix:** budget reservations and routing-rule token estimates counted an inline image by its base64 size (a 4 MB photo ≈ 1M tokens, enough to hit 402 `budget_exceeded` on a small budget); bodies of 32 KB or more are now walked for images, which count as 1 600 tokens each (`CostCalculator.ImageTokenEstimate`). The walk measures base64 on the parsed UTF-8 without copying it (about 1 µs on a 4 MB body). **Fix:** the PII scanner ran its patterns over `file_data`, `file_url` and `video_url` base64 strings, about 107 ms of CPU and 8 MB of allocations per 4 MB PDF, and with the `Redact` policy a chance match would have rewritten the file and corrupted it; those fields are now skipped like image data. README, *Attached files*, documents the 16 MB body limit (`Gateway__MaxRequestBodyBytes`, up to 100 MB) and what it means for video.

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
