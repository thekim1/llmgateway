# Umeå Kommun LLM Gateway – POC Implementation Plan

**Layout amendment — 2026-10-07:** At the user's request, the originally file-based
AppHost below is now a solution-owned project in `src/Ume.LlmGateway.AppHost`.
`Program.cs` contains the same resource graph, `Properties/launchSettings.json` owns
the launch profiles, and hosting packages use central package management. Root
`aspire.config.json` selects the project; the approved functional/security scope is unchanged.

## Problem & approach
Build a lean, self-hosted LLM gateway POC for Umeå kommun that does only what we need:
virtual API keys, cost attribution & limits per förvaltning/team/key, routing across providers
with fallback, PII guardrails, and an admin UI. Modelled on **Bifrost** (Go) concepts – virtual
keys, Customer→Team→VK governance, budgets with reset windows, weighted provider configs,
fallbacks – but implemented in **.NET 10**, orchestrated by the existing **Aspire 13.6 file-based
AppHost** (`apphost.cs`), runnable in Docker, on-prem first (cloud only for testing).

Current state: greenfield. Only an unwired `apphost.cs` + `aspire.config.json` and project-local
Aspire skills (`.agents/skills/*` – these take precedence for AppHost wiring via `aspireify`).
Toolchain present: .NET SDK 10.0.401, Aspire CLI 13.6, Node 24, Docker 29.

## Confirmed decisions
| Topic | Decision |
|---|---|
| Providers | Ollama (local), Ollama Cloud, Azure OpenAI (Sweden Central/EU), Azure AI Foundry, OpenAI, Anthropic, generic OpenAI-compatible (vLLM etc.) |
| Hierarchy | Förvaltning/Department → Team → Virtual key, budgets at every level |
| Admin auth | Generic OIDC; Keycloak container in Aspire for dev, swappable to Entra ID / AD FS |
| DB | PostgreSQL |
| Content logging | **Metadata only** – prompts/responses never persisted |
| PII guard | Yes; **optional, configured per virtual key** (Off / Allow / Redact / Block / Reroute to on-prem) |
| Frontend | Vue 3 + TypeScript + Vite + **Pinia** |
| Currency | SEK for budgets; provider prices stored in USD, configurable USD→SEK rate |
| Client API | OpenAI `/v1/chat/completions` (+SSE), `/v1/embeddings`, `/v1/models`, `/v1/responses`; Anthropic `/v1/messages` passthrough. Must handle future model families (e.g. GPT-6) |
| Budget exceeded | Hard block at 100 %, alerts at configurable thresholds (e.g. 50/80/100 %) |
| Key rotation | User chooses per rotation: **immediate revoke (default)** or 24 h grace |
| Rate limits | Yes, per VK (requests + tokens), Redis-backed; **Redis secured by default** |
| Provider classification | Each provider account tagged **OnPrem / EU-hosted / External (non-EU)** – drives PII routing |

## Architecture

```
 clients (apps, scripts, Copilot-like tools)
        │  Authorization: Bearer ume-sk-…   (OpenAI / Anthropic compatible)
        ▼
 ┌──────────────────┐   counters, rate limits, key cache (no content)   ┌───────┐
 │ Gateway (data    │ ─────────────────────────────────────────────────▶│ Redis │ (internal net only, pwd+ACL, TLS in compose)
 │ plane) ASP.NET   │                                                   └───────┘
 │ minimal API      │ ── usage metadata (async channel) ──▶ ┌────────────┐
 └────────┬─────────┘                                       │ PostgreSQL │ (internal net only, least-priv roles)
          │ provider adapters + Polly fallback               └─────▲──────┘
          ▼                                                        │
 Ollama(local) · vLLM · Azure OpenAI · AI Foundry · OpenAI · Anthropic · Ollama Cloud
                                                                   │
 ┌──────────────┐  cookie (BFF)  ┌────────────────────────┐  EF Core │
 │ Admin UI     │ ─────────────▶ │ Admin API (control     │──────────┘
 │ Vue3 + Pinia │                │ plane, OIDC/BFF, RBAC) │◀── OIDC ── Keycloak (dev) / Entra ID (prod)
 └──────────────┘                └────────────────────────┘
 MigrationService (worker): applies EF migrations + dev seed, then exits.
```

Data plane and control plane are **separate services** (blast-radius reduction; gateway has no
admin endpoints, admin API never proxies LLM traffic).

### Solution layout
```
apphost.cs                               (existing, file-based AppHost – wire resources here)
UmeLlmGateway.slnx
Directory.Build.props / Directory.Packages.props   (central package mgmt, nullable, warnings-as-errors, analyzers)
src/
  Ume.LlmGateway.ServiceDefaults/        OTel, health checks, resilience, service discovery
  Ume.LlmGateway.Domain/                 entities, budget math, pricing, routing, PII detection, key gen (pure, no I/O)
  Ume.LlmGateway.Infrastructure/         EF Core (Npgsql), Redis stores, Data Protection, provider adapters
  Ume.LlmGateway.Gateway/                data plane: /v1/* endpoints
  Ume.LlmGateway.AdminApi/               control plane: /api/* + BFF auth, serves SPA in prod
  Ume.LlmGateway.MigrationService/       migrations + seed
  admin-ui/                              Vue 3 + Vite + TS + Pinia + Vue Router + vue-i18n + Reka UI (headless) + own design tokens/themes
tests/
  Ume.LlmGateway.Domain.Tests/           xUnit v3 unit tests
  Ume.LlmGateway.Gateway.Tests/          WebApplicationFactory + WireMock.Net fake providers
  Ume.LlmGateway.AdminApi.Tests/
  Ume.LlmGateway.IntegrationTests/       Aspire.Hosting.Testing (real Postgres/Redis containers)
  admin-ui unit tests                    Vitest + Vue Test Utils (in admin-ui/)
  e2e/                                   Playwright (TS) against the Aspire-started stack
deploy/
  docker-compose (generated via `aspire publish` Docker Compose target) + hardening overrides
docs/
  architecture.md, security-and-compliance.md, runbook.md, demo-script.md
```

## Domain model (Postgres)
- **Department** (förvaltning) – name, cost-centre code (ansvarskod), active.
- **Team** – belongs to Department.
- **VirtualKey** – belongs to Team; `prefix` (`ume-sk-xxxx` shown in UI), **HMAC-SHA256 hash** of full key
  (server-side pepper), name, owner, expiry, active, allowed models/aliases, allowed residency classes,
  PII policy, rate-limit config, rotated-from/rotated-to link, grace-until.
  Plain key shown **once** at create/rotate, never stored.
- **ProviderAccount** – type (OpenAI, AzureOpenAI, AzureAIFoundry, Anthropic, Ollama, OllamaCloud,
  OpenAICompatible), base URL, auth mode (api-key / Entra managed identity / none), **encrypted credential**
  (ASP.NET Data Protection), **Residency: OnPrem | EU | External**, capability flags
  (chat, embeddings, responses, messages, streaming), enabled.
- **Model / Deployment** – provider model id (data-driven, *not* hardcoded → new families like GPT-6 are
  config, not code), capability/parameter profile (e.g. `max_completion_tokens` vs `max_tokens`,
  reasoning params, temperature unsupported), context size.
- **ModelPrice** – USD per 1M input / cached-input / output / reasoning tokens, effective-from date
  (history kept so old usage stays correctly priced). On-prem models can have an internal cost (e.g. 0 or kWh-based).
- **Route (model alias)** – e.g. `ume/chat-standard` → ordered targets `[{deployment, weight, priority}]`;
  same-priority targets load-balanced by weight, lower priority = fallback chain. Clients may also call a
  concrete `provider/model` if the key allows it.
- **Budget** – scope (Department | Team | VirtualKey), limit SEK, period (day/week/month/quarter/year),
  calendar-aligned (Europe/Stockholm), alert thresholds. A request must pass **all** budgets up the chain.
- **ExchangeRate** – USD→SEK, effective-from (manual entry in POC).
- **UsageRecord** – metadata only: timestamp, key/team/department ids, route, provider, model,
  tokens (in/cached/out/reasoning), cost USD+SEK, latency, status, fallback-attempt count, PII action taken
  (category counts only, never the matched value), correlation id. Partition/retention-ready.
- **AlertEvent**, **AuditLog** (append-only, every admin mutation: who/what/when/before/after – secrets masked).

## Gateway request pipeline
1. **Limits**: max body size, header limits, request timeout, HTTPS only.
2. **Authenticate VK** from `Authorization: Bearer`, `x-api-key`, or `api-key` header → HMAC → lookup
   (Redis cache w/ short TTL + Postgres fallback). Constant-time compare, reject expired/inactive/past-grace.
3. **Authorize**: model/alias allowed for key; endpoint capability.
4. **Rate limit** (Redis, atomic Lua sliding/fixed window): requests/min and tokens/min per VK → 429 + `Retry-After`.
5. **Budget pre-check**: spend for current period at key, team, department (Redis counters, rebuilt from
   Postgres on cold start) + conservative reservation from `max_tokens`/estimate → 402/429-style error
   with clear message when over. Release/reconcile reservation after the call.
6. **PII guard** (if enabled on key): detectors for Swedish personnummer/samordningsnummer (with Luhn
   check), email, phone (SE formats), IBAN/bankgiro; pluggable for later NER/Presidio. Action per key:
   Allow, Redact (`[PERSONNUMMER]`), Block (400), or **RerouteToOnPrem** (restrict candidate targets to
   `Residency=OnPrem`). Separately, a key can be restricted to residency classes regardless of PII.
7. **Resolve route** → candidate list filtered by key allow-list, residency, provider enabled, circuit state.
8. **Invoke provider** via adapter; on 429/5xx/timeout/circuit-open → next target (Polly resilience +
   per-provider circuit breaker). Do **not** fall back on 4xx client errors. Headers out:
   `x-ume-provider`, `x-ume-model`, `x-ume-fallbacks`, `x-ume-cost-sek`, `x-request-id`.
9. **Streaming (SSE)**: stream-through with backpressure; inject `stream_options.include_usage` for
   OpenAI-family so usage is captured; count tokens via final usage chunk (fallback: tokenizer estimate).
   Fallback only possible before first byte is sent.
10. **Account**: compute cost (price table + exchange rate), increment Redis counters, enqueue
    UsageRecord to bounded channel → batched Postgres writer; evaluate alert thresholds.

### Provider adapters & API compatibility
- Request/response bodies handled as **`JsonNode` passthrough** – only fields we need are read/rewritten;
  unknown fields (new GPT-6 params, tools, reasoning options, etc.) pass through untouched.
- Per-model **parameter profile** applies rewrites (e.g. `max_tokens`→`max_completion_tokens`, drop
  unsupported `temperature`) – data-driven so new families are config.
- `OpenAICompatibleAdapter` – OpenAI, Ollama (`/v1`), Ollama Cloud, vLLM, Azure AI Foundry
  (OpenAI-compatible inference endpoint), Azure OpenAI v1 API (`api-key` or Entra token).
- `AnthropicAdapter` – translate OpenAI chat ⇄ Anthropic Messages (incl. streaming events, tool calls, usage).
- `/v1/responses` – passthrough to providers with `responses` capability (OpenAI, Azure OpenAI); 400 otherwise.
- `/v1/messages` – passthrough to Anthropic-type providers only (same auth/budget/PII/accounting pipeline).
- `/v1/models` – lists aliases/models the calling key may use.
- Optional **Fake provider** (WireMock.Net or tiny stub container) for demos & tests without real keys.

## Admin API & UI
- Auth: OIDC code flow + PKCE in **BFF pattern** (HttpOnly, Secure, SameSite=Lax cookie for OIDC redirects; no tokens in
  browser), antiforgery on mutations. Roles from OIDC claims: `gateway-admin`, `department-admin`
  (scoped to own förvaltning), `viewer` (read-only usage).
- Endpoints: departments, teams, virtual keys (create → show once, rotate [immediate | 24 h grace],
  revoke, enable/disable), provider accounts (credentials write-only, never returned), models & prices,
  routes/fallbacks, budgets & thresholds, exchange rate, usage/cost reports (filters + CSV export),
  alerts, audit log. Validation via FluentValidation or built-in .NET 10 validation; ProblemDetails errors.
- UI pages (Vue/Pinia), role-aware navigation:
  - *Översikt*: spend vs budget per förvaltning/team, top keys, provider health summary.
  - *Organisation*: Förvaltningar & Team. *Nycklar*: virtual keys (copy-once dialog, rotate dialog with
    "revoke now" (default) / "24 h grace").
  - *Leverantörer & modeller*: providers (residency badge), models/prices, routes (ordered fallback editor).
  - *Budgetar & larm*, *Användning* (usage explorer + CSV), *Granskningslogg* (audit).
  - *Kom igång* (developer portal), *Modellkatalog*, *Drift/Hälsa* (ops), *Inställningar* (theme, language).
  Swedish UI text (`vue-i18n` sv/en).

## UI design system & accessibility (WCAG 2.2 AAA target)
Reference: Umeå kommun "Grafisk profil för digitala gränssnitt" (2019) – used as **inspiration only**.
Extracted: mörkgrön `#006E1E`, gråsvart `#555555` (text), brisvit `#D1E8FF`, rosa `#E4B1C2`, vit;
Calibri; Google Material Symbols **Sharp**; 12-col grid (6 on mobile), 20 px gutters; links underlined.

- **Target WCAG 2.2 AAA** (exceeds the legal AA baseline in Lag (2018:1937) om tillgänglighet till digital
  offentlig service / EN 301 549). Where a AAA criterion can't sensibly apply, document why in the
  accessibility statement (tillgänglighetsredogörelse).
  - Contrast ≥ 7:1 text, ≥ 4.5:1 large text, ≥ 3:1 UI components/focus, in **every theme**.
    Note: `#006E1E` on white ≈ 6.5:1 → AA only. Use a darkened brand green (≈ `#00561A`, verified by test)
    for text/links/buttons; keep `#006E1E` for large headings/surfaces.
  - Focus Appearance (2.4.13), target size ≥ 44×44 px (2.5.5), no time limits / session-expiry warning with
    extend (2.2.3/2.2.6), no motion by default (2.3.3), contextual help + labels/instructions on every
    form (3.3.5), error prevention with review/confirm/undo for all destructive actions (3.3.6),
    section headings (2.4.10), link purpose from link text alone (2.4.9), consistent help location (3.2.6),
    accessible authentication without cognitive tests (3.3.9 – OIDC/passkeys).
  - Plain Swedish (klarspråk), glossary for technical terms (virtuell nyckel, budget, reserv-leverantör),
    reading level kept simple; `lang` attributes; i18n sv (default) + en.
  - Charts always paired with an accessible data table / text summary; no information by colour alone
    (icons + text for status: OK / Varning / Fel).
  - Respects `prefers-color-scheme`, `prefers-contrast`, `prefers-reduced-motion`, `forced-colors`
    (Windows High Contrast). Text resizable to 200 %/reflow at 320 px, spacing overrides (1.4.12).
- **Themes** (CSS custom-property design tokens, switchable, persisted per user, default = system):
  1. *Umeå Ljus* (brand light), 2. *Umeå Mörk* (dark mode, green tuned for dark backgrounds),
  3. *Hög kontrast ljus*, 4. *Hög kontrast mörk*. Each theme's token pairs are verified by an
  automated contrast unit test (fails build below AAA thresholds).
- **Typography**: font stack `Calibri, Carlito, "Segoe UI", system-ui, sans-serif` (Carlito = OFL,
  metric-compatible, self-hosted – no external font CDN for privacy). Monospace stack for keys/code.
- **Icons**: Material Symbols Sharp, self-hosted subset; always with visible text or `aria-label`.
- **Components**: headless, WAI-ARIA-compliant primitives (**Reka UI**, ex Radix Vue) + our own styled
  component layer bound to tokens – full control of a11y and theming (instead of a heavily styled kit).
  Layout uses the 12-col grid concept but fluid/wider for data-dense admin views.
- **UX principles**: task-oriented navigation ("Skapa nyckel", "Se kostnader"), clear empty states that
  explain the next step, wizards for multi-step tasks (create team + key + budget), inline validation with
  human-readable messages, confirm dialogs that state consequences (e.g. "Appar som använder nyckeln slutar
  fungera direkt"), toast + persistent status region (`aria-live`), skip links, breadcrumbs.

## Developer experience (teams integrating against the gateway)
- **"Kom igång" / developer portal** in the UI: base URL, how to authenticate, copy-paste snippets per key
  & model alias (curl, .NET `OpenAI` SDK / `Microsoft.Extensions.AI`, Python, JS/TS, Anthropic SDK).
- **OpenAPI** documents for gateway and admin API (`Microsoft.AspNetCore.OpenApi`) + **Scalar** API
  reference UI; downloadable spec.
- **Model catalogue** page: aliases, underlying models, residency badge (On-prem / EU / Extern), price per
  1M tokens in SEK, capabilities (streaming, tools, embeddings, responses), PII policy effects.
- **Predictable errors**: OpenAI-compatible error JSON with stable `code`s (`budget_exceeded`,
  `rate_limited`, `pii_blocked`, `model_not_allowed`, `key_expired`, `all_providers_failed`), human message,
  `request_id`, docs link. OpenAI-style `x-ratelimit-*` headers + `x-ume-budget-remaining-sek`.
- **Request lookup** by `request_id` (metadata only) for debugging; per-key usage view for team members.
- **Playground** (optional, behind a flag): test a key/alias from the UI; nothing stored, PII policy applies,
  clear notice about data handling.

## Operations experience (infra techs running & upgrading prod)
- **Drift/Hälsa page**: provider status, circuit-breaker state, latency p50/p95, error & fallback rates,
  Postgres/Redis health, queue depth of usage writer, app + DB schema version.
- Actions: disable/drain a provider, force circuit open/closed, invalidate key cache – all audited.
- **Health endpoints** `/health/live`, `/health/ready`; `/version`; OTLP export (Grafana/Prometheus/Loki on-prem)
  with example dashboards; structured JSON logs (no content).
- **Config as code**: export/import providers, models, prices, routes (JSON/YAML, secrets referenced by env
  var / secret name only) → GitOps-friendly, repeatable environments.
- **Upgrades**: semver images, explicit migration job (expand/contract, backwards-compatible migrations),
  CHANGELOG + upgrade guide per release, startup config validation (`ValidateOnStart`) with actionable
  errors, Postgres backup/restore scripts, Data Protection key-ring backup guidance (losing it = losing
  provider credentials).
- **Docs**: runbook, upgrade guide, troubleshooting, ADRs for key decisions.

## Security & compliance (sensitive municipal data)
Legal frame to document (validate with DPO/jurist – plan does not constitute legal advice):
GDPR + Dataskyddslag (2018:218), Offentlighets- och sekretesslagen (OSL 2009:400 – sekretess when
data leaves to external providers), Arkivlagen / gallring decisions for logs (usage records may be
allmän handling), Cybersäkerhetslagen (NIS2), EU AI Act (transparency/logging), third-country transfer
rules (Schrems II/EU-US DPF) for non-EU providers. Deliver a **DPIA input sheet** and records-of-processing
draft in `docs/security-and-compliance.md`.

Technical controls:
- **Data minimisation**: no prompt/response persistence; OTel/logging configured to never capture bodies
  or auth headers (redaction enricher + tests asserting it); PII-category counts only.
- **Residency enforcement**: providers tagged OnPrem/EU/External; keys can be restricted; PII can force on-prem.
- **Secrets**: VK stored as HMAC hash; provider creds encrypted with Data Protection (key ring persisted
  to Postgres, protected by certificate on-prem); dev secrets via Aspire secret parameters/user-secrets;
  nothing secret in repo or images.
- **Transport**: HTTPS everywhere, HSTS, TLS to providers; Redis/Postgres TLS in compose profile.
- **Redis secured by default**: password (Aspire-generated secret parameter) + ACL user with only needed
  commands (no `FLUSHALL`/`CONFIG`/`KEYS`), protected-mode, **no host port published**, on a dedicated
  internal Docker network shared only with the gateway; only counters/hashed-key cache stored (no content);
  key-prefix namespacing; TLS in compose/on-prem.
- **Postgres**: separate migrator (DDL) and app (DML) roles, no published port in compose, data volume.
- **Network segmentation (compose)**: `edge` (gateway, admin-api), `data` (internal: postgres, redis),
  `llm` (gateway → ollama/vLLM). Admin API not exposed on the gateway's public listener.
- **Containers**: .NET chiseled/distroless non-root images, read-only root FS, dropped capabilities,
  health checks, pinned image digests; SBOM + `dotnet list package --vulnerable` + npm audit in CI.
- **AppSec**: input size limits, strict JSON parsing, CSP/X-Frame-Options/Referrer-Policy on UI,
  CORS locked down, rate limiting on admin API too, audit log, security headers tests.
- **Key hygiene**: show-once, prefix for identification, expiry, rotation w/ optional grace, instant revoke
  (Redis cache invalidation via pub/sub), last-used timestamp.

## Aspire wiring (`apphost.cs`, via project-local `aspireify` skill)
- `postgres` (+ data volume, `gatewaydb`), `redis` (password secret, no external port), `keycloak`
  (dev realm import with test users/roles), `ollama` (CommunityToolkit integration, tiny model e.g.
  `qwen2.5:0.5b` for demos/tests), optional `fake-llm`.
- `migrations` → `gateway` and `adminapi` `WaitForCompletion(migrations)`.
- `admin-ui` via `AddViteApp` (Vite proxy → adminapi in dev; built static assets served by adminapi in prod).
- Secret parameters for provider credentials (OpenAI, Azure, Anthropic, Ollama Cloud) – optional, providers
  without credentials are seeded disabled.
- Docker Compose publish target for on-prem/cloud test (`aspire publish`), plus a hardening override file.
- Use `aspire docs search` / `aspire docs api search` before editing AppHost APIs; lifecycle via `aspire start`.

## Testing strategy
- **Unit (xUnit v3 + Shouldly + NSubstitute)**: key gen/hash/verify, rotation & grace logic, budget periods
  (calendar aligned, Europe/Stockholm, DST edges), hierarchical budget checks, cost calc (cached/reasoning
  tokens, price history, SEK conversion), route selection (weights, priority, residency filter, circuit
  state), PII detectors (valid/invalid personnummer, samordningsnummer, Luhn), parameter-profile rewrites,
  JSON passthrough preserves unknown fields, Anthropic translation.
- **Gateway component tests**: WebApplicationFactory + WireMock.Net providers: fallback on 429/5xx/timeout,
  no fallback on 400, streaming passthrough + usage capture, budget block, rate-limit 429, PII
  block/redact/reroute, revoked/expired key, logs contain no body/auth header.
- **Integration (Aspire.Hosting.Testing)**: full stack with Postgres+Redis(+fake LLM): create key via admin
  API → call gateway → usage recorded → budget enforced; Redis requires auth.
- **Frontend**: Vitest for Pinia stores/components + `vitest-axe` on components; **design-token contrast
  test** asserting AAA ratios for every theme. **Playwright** e2e: login via Keycloak, create
  department/team/key (copy-once), rotate key, set budget, view usage after gateway call, audit log entry,
  developer-portal snippet copy, ops health page.
- **Accessibility e2e**: `@axe-core/playwright` scan (tags `wcag2a/aa/aaa`, `wcag21*`, `wcag22*`) on every
  page × every theme; keyboard-only journey tests (tab order, focus visible, no traps), 200 % zoom/320 px
  reflow, reduced-motion & forced-colors emulation. Plus a manual checklist (screen reader NVDA/VoiceOver)
  in docs since automation can't cover all AAA criteria.
- CI-ready scripts (`dotnet test`, `npm run test`, `npx playwright test`); GitHub Actions/Azure DevOps file optional.

## Todos (tracked in SQL)
1. `scaffold-solution` – slnx, Directory.Build/Packages.props, analyzers, .editorconfig, .gitignore, git init.
2. `service-defaults` – ServiceDefaults project (OTel w/o body capture, health, resilience).
3. `domain-model` – Domain entities + value objects + pure services (keys, budgets, pricing, routing, PII). (dep 1)
4. `domain-tests` – unit tests for domain. (dep 3)
5. `persistence` – EF Core Npgsql DbContext, configs, migrations, Data Protection key store. (dep 3)
6. `migration-service` – worker applying migrations + dev seed (dept/team/key/providers/routes/prices). (dep 5)
7. `redis-stores` – rate limiter, budget counters, key cache, revoke pub/sub. (dep 3)
8. `provider-adapters` – OpenAI-compatible, Azure OpenAI, AI Foundry, Ollama/Ollama Cloud, Anthropic, passthrough JSON, param profiles. (dep 3)
9. `gateway-api` – /v1 endpoints + pipeline (auth, rate limit, budget, PII, routing, fallback, streaming, accounting). (dep 5,7,8, 2)
10. `gateway-tests` – component tests with WireMock.Net. (dep 9)
11. `admin-api` – BFF OIDC, RBAC, CRUD, rotate/revoke, reports, audit log, alerts. (dep 5,7, 2)
12. `admin-api-tests` (dep 11)
13. `ui-design-system` – Vite+Vue3+TS scaffold, design tokens, 4 themes, Reka UI-based components, a11y primitives, i18n, contrast test. (dep 1)
14. `admin-ui` – admin pages (org, keys, providers/models/routes, budgets, usage, audit, settings). (dep 11,13)
15. `developer-portal` – Kom igång snippets, model catalogue, Scalar/OpenAPI links, request lookup, optional playground. (dep 9,14)
16. `ops-experience` – Drift/Hälsa page + ops API (provider drain, circuit control, cache invalidate), /version, config export/import. (dep 9,11,14)
17. `admin-ui-unit-tests` – Vitest + vitest-axe. (dep 14,15,16)
18. `apphost-wiring` – Postgres, Redis (secured), Keycloak realm, Ollama, fake-llm, projects, Vite app, secret params. (dep 6,9,11,14)
19. `integration-tests` – Aspire.Hosting.Testing end-to-end. (dep 18)
20. `playwright-e2e` – UI flows + axe AAA scans per page × theme, keyboard/zoom/forced-colors. (dep 15,16,18)
21. `docker-hardening` – Dockerfiles/chiseled images, compose publish, networks, Redis ACL/TLS, Postgres roles. (dep 18)
22. `docs-compliance` – architecture, security & compliance (GDPR/OSL/NIS2/AI Act, DPIA input), accessibility statement, runbook, upgrade guide, ADRs, demo script. (dep 18)
23. `verify-end-to-end` – `aspire start`, run all tests, demo flow incl. fallback, budget block, PII reroute, theme switch. (dep 10,12,17,19,20,21,22)

## Notes / risks
- Budget enforcement under concurrency is "reserve-then-reconcile"; small overshoot possible on streaming –
  acceptable for POC, documented.
- Anthropic ⇄ OpenAI translation is the most complex adapter; scope to text, tools, streaming, usage.
- Token counting for providers that omit usage in streams uses estimation (documented).
- Ollama model pull on first start can be slow; tests use the fake provider by default, Ollama is opt-in.
- Out of scope for POC: semantic caching, MCP gateway, SSO for gateway clients (keys only), automated
  exchange-rate feed, invoicing integration (Raindance etc.) – CSV export per ansvarskod instead.
