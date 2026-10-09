# Handover – Umeå kommun LLM Gateway POC

**Read first:**
- `docs/implementation-plan.md` – the approved plan. It is the source of truth for scope, security, WCAG AAA and testing.
- `docs/admin-api.md` – the admin API contract the Vue UI is built against.

## Goal
Build a self-hosted LLM gateway POC in .NET 10, modelled on Bifrost:
- Virtual API keys that are easy to rotate (revoke immediately, or keep a 24 h grace period).
- Budgets in SEK per förvaltning, team and key.
- Routing across multiple providers with fallback.
- A PII guard set per key.
- Metadata-only logging.

Also required:
- A Vue 3 + Pinia + Tailwind admin UI meeting WCAG 2.2 AAA, with light, dark and Lumen themes.
- Orchestration by the Aspire 13.6 project (`src/Ume.LlmGateway.AppHost`) and Docker Compose publish.
- Compliance with GDPR, OSL, NIS2 and the AI Act; hosted on-prem.
- Unit, component and integration tests, plus Playwright e2e with axe AAA scans.

## Status
| Area | Status |
|---|---|
| Solution scaffold, `Directory.Build.props` / `Directory.Packages.props` (central packages, warnings as errors) | ✅ |
| `Ume.LlmGateway.ServiceDefaults` (OTel, `/health/live`, `/health/ready`, `/version`) | ✅ |
| `Ume.LlmGateway.Domain` + `tests/Ume.LlmGateway.Domain.Tests` (71 passing) | ✅ |
| `Ume.LlmGateway.Infrastructure` (EF/Npgsql, migrations `InitialCreate` and `ModelFeatures`, Redis and in-memory stores, Data Protection, provider adapters including Anthropic⇄OpenAI translation) | ✅ Postgres/provider paths covered by gateway tests; Redis awaits integration tests |
| `Ume.LlmGateway.MigrationService` (migrate + dev seed) | ✅ builds |
| `Ume.LlmGateway.FakeLlm` (fake OpenAI/Anthropic provider for demos/tests) | ✅ builds |
| `Ume.LlmGateway.Gateway` (full pipeline in `Pipeline/`, `Program.cs`) | ✅ component-tested; bounded body reads and stable 415 errors fixed |
| `tests/Ume.LlmGateway.Gateway.Tests` | ✅ 57 passing, including signed live operations, writer retry/recovery and production HTTPS enforcement |
| `Ume.LlmGateway.AdminApi` | ✅ BFF, RBAC, CRUD, reports, live ops, transactional config import and audit; 35 tests passing |
| `src/admin-ui` | ⚠️ Rebuilt 2026-10 from `Design prototype/` (Vue 3 + Pinia + Tailwind 3, English only, light/dark/Lumen, reusable `components/ui`). Typecheck, lint, build and 7 unit tests pass. Playwright specs in `tests/e2e` still target the old Swedish UI and need updating; old store/component specs were removed and need replacing |
| AppHost wiring | ✅ HTTPS stack starts healthy; migrations finish before APIs; live fallback and PII rerouting verified |
| Integration tests | ✅ 6 passing: real FakeLlm + Postgres + authenticated Redis, API-to-gateway accounting, budgets/rotation, invalidation, rate limits and atomic reservations |
| Playwright | ✅ 12 real-stack browser/accessibility/demo tests passing |
| Docker hardening | ✅ Step 7: Aspire-generated Compose + mandatory override; live TLS/ACL/role/container/SPA checks pass |
| Docs, final verification | ✅ POC steps 8–9 complete; manual accessibility/legal/production approvals remain operator release gates |

## Conventions and gotchas
- **Toolchain:** .NET SDK 10.0.401, Aspire CLI 13.6, Node 24, Docker 29, Windows/PowerShell 5.1. Use `;` instead of `&&`.
- **Tests:** run with `dotnet test --project <path>` (Microsoft Testing Platform, configured in `global.json`). xUnit v3 + Shouldly; `tests/Directory.Build.props` adds the packages.
- **Build check:** `dotnet build <proj> -v q -nologo 2>&1 | Select-String "error|Build succeeded"`.
- **Analyzers:** `TreatWarningsAsErrors` with `AnalysisLevel latest-recommended`. Many CA rules are already in NoWarn (see `Directory.Build.props`). Use `[LoggerMessage]` source-generated logging.
- **Package versions:**
  - Aspire.* 13.6.0; EF Core / ASP.NET Core 10.0.12; Npgsql EF 10.0.3; Scalar 2.17.14.
  - WireMock.Net 2.18.0; Testcontainers 4.15.0 (the parameterless `PostgreSqlBuilder()` ctor may be obsolete, so use `new PostgreSqlBuilder("postgres:17-alpine")`).
  - Keycloak hosting exists only as `13.6.0-preview.1.26479.8`.
  - Aspire hosting packages use `Directory.Packages.props`; the AppHost SDK is pinned to 13.6.0 in its project file and implicitly provides `Aspire.Hosting.AppHost`.
- **AppHost:** use the project-local skills in `.agents/skills` (`aspireify`, `aspire-orchestration`) and `aspire docs search` before using AppHost APIs.
- **C# parser quirk:** `case "x" when a?["b"] is { } y:` fails to parse; use if/else instead.
- **Display artefact:** `Enums.cs` contains `Bearer = 1` (some viewers redact it).

## Key design (already implemented)
**Keys**
- Format `ume-sk-…`; only the HMAC-SHA256 hash is stored, using the pepper from `Security:KeyPepper` (≥32 chars, validated on start).
- `VirtualKeyHasher` is a singleton.
- `VirtualKey.GetStatus(now)` returns Active / InGracePeriod / Expired / Revoked / Disabled.

**Provider credentials**
- Encrypted by `CredentialProtector` (Data Protection, key ring stored in Postgres; optional certificate via `DataProtection:CertificatePath`).

**Stores** (Redis if `ConnectionStrings:redis` is set, otherwise in-memory)
- `IRateLimiter`: fixed window per minute.
- `ISpendLedger`: micro-SEK counters, reserve then reconcile; `TryReserveAsync` returns -1 on success or the index of the exhausted counter.
- `IInvalidationBus`: kinds `Keys` and `Config`; Redis channel `ume:invalidate`. The admin API must publish on every key or config change.
- `ICircuitBreakerStore`: also supports forced open/closed.

**Gateway pipeline** (`src/Ume.LlmGateway.Gateway/Pipeline/GatewayRequestHandler.cs`)
1. Request id (`x-request-id`, server-generated).
2. Key from `Authorization: Bearer`, `x-api-key` or `api-key`; `KeyAuthenticator` caches it in its own MemoryCache and drops the cache on a bus `Keys` message.
3. Requires JSON content type (415 otherwise) and a JSON object with `model` (400).
4. `GatewayCatalog` snapshot resolves the alias or concrete deployment: 404 if missing, 403 `model_not_allowed`, 400 on kind mismatch.
5. Rate limit: 429 with `Retry-After` and `x-ratelimit-*` headers.
6. PII guard per key (Off / Allow / Redact / Block / RerouteToOnPrem); sets header `x-ume-pii`.
7. `RouteSelector.Order` builds the candidate list (residency, capabilities, streaming; circuit-open providers moved last); 503 `no_eligible_provider` if empty.
8. Budget reservation across key, team and department; 402 `budget_exceeded`.
9. Attempt loop, falling back on:
   - retryable failures: 408, 409, 429, 5xx, timeouts (504) and connection errors (502);
   - provider configuration errors: 401, 403, 404.

   Other 4xx responses pass through with no fallback. If every attempt fails: 502/504 `all_providers_failed`.
10. SSE streaming passthrough; no fallback after headers are sent.
11. Accounting: `CostCalculator`, ledger commit, rate-limit tokens, `UsageWriter` (bounded channel → batched Postgres writes, `LastUsedAt`, de-duplicated `AlertEvent`s, optional webhook `Gateway:AlertWebhookUrl`), `GatewayMetrics` (Meter `Ume.LlmGateway`).
12. Response headers: `x-ume-provider`, `x-ume-model`, `x-ume-residency`, `x-ume-fallbacks`, `x-ume-cost-sek`, `x-ume-budget-remaining-sek`.
13. Errors use the OpenAI shape `{error:{message,type,code,param,request_id,doc_url}}`, except `/v1/messages`, which uses the Anthropic shape (`GatewayErrors.cs`). Messages are in Swedish.

**Endpoints**
- `/v1/chat/completions`, `/v1/embeddings`, `/v1/responses`, `/v1/messages`, `/v1/models`.
- OpenAPI at `/openapi/v1.json`, Scalar at `/scalar`.
- Security headers are set; the Kestrel Server header is off.
- `public partial class Program;` is present for WebApplicationFactory.

**Gateway options**
- `Gateway:KeyCacheSeconds` and `Gateway:CatalogCacheSeconds`: set both to 0 in tests.
- `DefaultOutputTokenEstimate`, `MaxRequestBodyBytes`, `DocsUrl`.

**Dev seed** (`MigrationService/DevSeeder.cs`, runs when `Seed:Enabled` is true)
- 3 departments, 4 teams.
- Fake providers on `Seed:FakeLlmUrl`.
- `ollama-local` when `Seed:OllamaUrl` is set.
- Real providers are enabled only when a credential is configured.
- Routes: `ume/chat-standard`, `ume/chat-onprem`, `ume/chat-advanced`, `ume/demo-fallback`, `ume/embeddings`.
- Exchange rate 9.50 SEK/USD.
- Dev key from `Seed:DevKey` (PII policy RerouteToOnPrem, 500 SEK budget).

**FakeLlm** (behaviour is chosen by model name)
- `fake-fail-503` (Anthropic endpoint returns 529), `fake-fail-429`, `fake-slow` (3 s delay).
- Echoes the last user message.

## Next steps (in order)
1. **Gateway tests — completed 2026-10-07.** 50 passing gateway/provider tests; domain regression suite: 71 passing.
   - Fixed Postgres budget boundaries by normalizing persisted/query DateTimeOffset values to UTC while preserving Stockholm calendar calculations.
   - Fixed SSE empty data-line handling, bounded request-body reads (including non-Kestrel hosts), and 415 gateway error shape.
   - Standard WireMock mappings explicitly use priority 10; stream mappings use 1.
   - Implemented scope and fixture details (retained as reference):
   - **Fixture** (xUnit v3 `[assembly: AssemblyFixture]`):
     - Testcontainers Postgres, WireMock upstream, and `WebApplicationFactory<Program>` with environment `Testing`.
     - Settings via `UseSetting`: `ConnectionStrings:gatewaydb`, `Security:KeyPepper`, and both cache settings at 0.
     - A capturing `ILoggerProvider` at Trace level.
     - Migrate, then seed WireMock providers: on-prem / EU / external / anthropic, plus a 1-second-timeout provider for the "slow" model.
     - A helper that creates a key per test, each with its own team.
   - **Cover:**
     - Keys: 401 missing / revoked / expired; 403 disabled; grace-period key still works.
     - Success: headers, cost, and the DB usage record (call `UsageWriter.FlushAsync`).
     - Fallback on 503 / 429 / timeout; no fallback on 400; all-fail → 502.
     - Streaming: usage captured; the usage chunk is hidden unless `include_usage` was requested.
     - Budgets: key and team → 402. Rate limit → 429.
     - PII: block, redact (upstream body has no personnummer, e.g. `19121212-1212`) and reroute → on-prem; external-only route → 503.
     - Request validation: model not allowed / not found, invalid JSON, 415.
     - Embedding alias on the chat endpoint → 400.
     - Anthropic: translation to the OpenAI shape, `/v1/messages` passthrough, Anthropic error shape.
     - `/v1/models` filtering; security headers.
     - **Logs never contain the prompt, the key plaintext or the provider secret.**
   - **Unit tests:** `AnthropicTranslator`, `AnthropicStreamTranslator`, `UsageParser`, `SseReader`.
   - **WireMock tip:** match on upstream model via `JsonPartialMatcher`. Give the stream mapping a higher priority (`AtPriority(1)`; lower value wins). Fix any bugs the tests reveal.
2. **AdminApi — completed 2026-10-07** (22 passing Postgres component tests; contract in `docs/admin-api.md`).
   - Runtime OIDC/Keycloak login still needs AppHost/e2e verification. Test authentication is confined to the test assembly.
   - Scoped queries enforce department access; explicit DTOs exclude key hashes and credentials. Mutations validate antiforgery and publish invalidation.
   - Key rotation inherits predecessor budgets/spend; reservations reject amounts exceeding remaining spend (both Redis Lua and in-memory implementations). Gateway suite now has 52 tests.
   - Remaining deployment-specific concerns (real OIDC/TLS, shared Redis, SPA assets, live gateway health) are verified in later steps.
   - Implemented scope (retained as reference):
   - Auth: OIDC + cookie BFF (`/bff/login|logout|user`), cookie `__Host-ume-admin`, **SameSite=Lax** (update the doc, which says Strict). Antiforgery via the `XSRF-TOKEN` cookie and `X-XSRF-TOKEN` header.
   - Roles: `gateway-admin`, `department-admin` (scoped by the `departmentCodes` claim), `viewer`.
   - CRUD for everything; keys show the secret once; rotate and revoke via `KeyRotation`.
   - Provider credentials are write-only (`hasCredential`).
   - Usage reports, CSV export and request lookup.
   - Ops: circuit control, drain, cache invalidation, config export/import.
   - Audit log with masked secrets; publish to `IInvalidationBus` on every change.
   - Serve the SPA from wwwroot in production, with a CSP. Add `key_disabled` to the doc's error list.
   - **Tests** in `tests/Ume.LlmGateway.AdminApi.Tests`, using a test auth handler.
3. **Admin UI — completed 2026-10-07.**
   - `npm ci`, lint with zero warnings, typecheck, 191 tests and production build pass.
   - Fixed theme labels, zero-budget status, review completeness, wizard budget defaults,
     organisation lookup refresh, malformed-response handling and explicit CSRF failures.
   - Added API/store/form/component tests, including acknowledgement, permission scope and axe checks.
   - Material Symbols Sharp is self-hosted as a 69-icon subset (231 KB instead of 3.5 MB).
     Regenerate after adding icon names with `python src/admin-ui/scripts/subset-icons.py`
     (requires `fonttools[woff]`; not needed for ordinary UI builds).
   - Admin API session renewal tests pass; suite now has 26 tests.
   - Full page/theme accessibility, browser behaviour and real OIDC remain ordered steps 4–6.
   - Vite proxies `/api`, `/bff` and `/signin-oidc` to `services__adminapi__http__0` (fallback `http://localhost:5180`), on port 5173.
4. **AppHost — completed 2026-10-07** (`apphost.cs`):
   - Aspire 13.6 Postgres 17 with volume, password-protected Redis with automatic local TLS,
     Keycloak realm/claim mappers and generated development users, FakeLlm, APIs and HTTPS Vite.
   - Generated pepper, development key, Redis password, OIDC secret and user password are secret
     parameters persisted only in the AppHost development secret store, never in repo or logs.
   - Ollama is opt-in via `Ollama__Enabled=true`; uses `qwen2.5:0.5b` and a data volume.
   - `aspire start` and readiness passed; migrations finished before the APIs. Live requests verified
     fallback (`fake-eu`, one fallback) and PII rerouting (`fake-onprem`).
   - Vite prefers injected HTTPS service URLs; BFF `/bff/user` works and login returns an OIDC redirect.
     Full browser login/logout and role journeys still belong to step 6.
   - Local host-run APIs require ephemeral loopback Postgres/Redis endpoints. There are no fixed public
     database/cache ports; Compose must remove host publication entirely in step 7.
   - Stack was stopped cleanly. Gateway 52, Admin API 26 and UI 191 tests pass after wiring.
   - Resources: Postgres (`gatewaydb`, volume); Redis (password, no host port); Keycloak (realm `ume`, `roles` and `departmentCodes` mappers, test users); FakeLlm; Ollama opt-in.
   - Projects: migrations, then gateway and adminapi with `WaitForCompletion`.
   - Vite app on 5173.
   - Secret parameters: pepper (generated, 48+ chars) and dev key `Seed__DevKey`.
   - Set `Seed__FakeLlmUrl` to the fake-llm endpoint + `/v1`.
   - Validate with `aspire start`.
5. **Integration tests — completed 2026-10-07.** `tests/Ume.LlmGateway.IntegrationTests`:
   - Aspire documents that file-based AppHosts cannot use `DistributedApplicationTestingBuilder`;
     used the approved Testcontainers fallback with shared Postgres 17/Redis 8 and a real Kestrel FakeLlm.
   - 6 passing tests cover admin key creation → gateway → usage lookup, hard budget block and rotation
     inheritance, warm-cache revocation over Redis pub/sub, rate limits, fallback/PII routing,
     unauthenticated Redis rejection, and atomic concurrent reservations.
   - FakeLlm exposes its Program marker for hosting tests. Seed uses the real DevSeeder.
   - Test Redis receives a random password through environment/config stdin, not process arguments.
6. **Playwright e2e — completed 2026-10-07** (`tests/e2e`):
   - Typecheck and the full 11-test browser/accessibility suite passed; see the latest
     continuation checkpoint below for verified coverage and runtime state.
   - User flows from the plan.
   - axe scans with the wcag2a/aa/aaa and wcag21/22 tags, on every page × every theme.
   - Keyboard, 320 px reflow, forced-colors and reduced-motion checks.
7. **Docker hardening — completed 2026-10-07:**
   - `deploy/generated/docker-compose.yaml` is generated by Aspire 13.6; always use
     `deploy/compose.hardening.yaml` as well. The generated base alone is not production-safe.
   - Production contains only Postgres, Redis, migrations, gateway and Admin API. Keycloak,
     FakeLlm and Vite's dev server are not deployed; production uses an external HTTPS OIDC IdP.
   - Mandatory override supplies verified TLS, Redis namespace/command ACL, migrator/gateway/admin
     DB roles, append-only audit permissions, internal data network, separate edge/llm networks,
     no database/cache host ports, nonroot/read-only/drop-all-capability services and HTTPS probes.
   - Chiseled-extra .NET 10.0.12 base images and infrastructure images are digest-pinned.
     Extra includes ICU/time-zone data needed for Swedish/Stockholm calendar behavior.
   - Vue assets are published into Admin API (`PublishWithContainerFiles`); the standalone
     container build uses `PublishAdminUi=true` after `npm run build`.
   - `deploy/Test-Deployment.ps1` builds real images and verifies the composed deployment:
     migration completion, healthy APIs, TLS-only Postgres, DML/DDL restrictions, immutable images,
     Redis authentication/ACL exclusions and production SPA route fallback. All checks passed.
     Generated test secrets/certificates exist only in a held Docker tmpfs volume and are cleaned up;
     no prompt/response/key files or raw environment snapshots were saved.
   - Production secrets load from operator-mounted per-service files; startup rejects missing
     certificate encryption, non-VerifyFull Postgres or unsecured Redis. No production secrets
     are generated/persisted by Aspire prepare/deploy in this workflow.
   - Generate the compose file with `aspire publish`, plus a hardening override.
   - Networks: `edge`, `data` (internal), `llm`.
   - Redis ACL/TLS; Postgres migrator and app roles.
   - Images: chiseled, non-root, read-only.
8. **Docs — completed 2026-10-07:**
   - Added README, architecture, security/compliance with DPIA and processing-record drafts,
     accessibility statement/manual checklist, runbook, upgrade guide, ADRs, demo and changelog.
   - Current official Swedish cybersecurity act and Commission AI Act/2026 Omnibus timeline
     checked; legal/operator approvals are explicitly not inferred from automated tests.
   - Added guarded database backup/restore tools, release dependency-audit/SBOM CI checks.
   - architecture, security-and-compliance (GDPR / OSL / NIS2 / AI Act / DPIA input), accessibility statement (tillgänglighetsredogörelse), runbook, upgrade guide, ADRs, demo script, README.
9. **Final verification — completed 2026-10-07:** see the final verified checkpoint below.

## Pause checkpoint — 2026-10-07
- Stopped at the user's request during **step 3**. Do not mark the UI or subsequent steps complete.
- Verified backend results: gateway **52 passing**, Admin API **22 passing**, domain **71 passing** before later additions to KeyRotation (rerun domain suite).
- UI: `npm ci` completed, zero reported vulnerabilities; existing contrast suite **161 passing**.
- Added Swedish/English locale dictionaries, an ESLint flat config, missing routed pages, shared CRUD forms,
  key create/edit/rotation/revocation and show-once flows, usage/audit/catalogue/portal/ops/settings pages.
- Last UI validation failed:
  - ESLint config imports **`@eslint/js`**, which is missing. Add it as a direct dev dependency using npm,
    then run lint with `--fix --max-warnings 0` and resolve remaining findings.
  - Typecheck found unused `api` in `KeyForm.vue` and a template closure typing error in `OpsView.vue`.
    Both were just fixed, **not yet rechecked**.
  - Build has not passed yet.
- Known remaining UI corrections before calling step 3 done:
  - Theme locale option names must match actual IDs: `umea-light`, `umea-dark`, `hc-light`, `hc-dark`;
    current dictionaries use different names.
  - Review/accessibility-test the new forms and pages, including checkbox targets, error summaries,
    route ordering, prices, wizard budget ownership, and show-once navigation handling.
  - Add frontend component/store/API tests; currently only the existing contrast tests run.
  - Verify changing an organisation refreshes dependent team choices and that all CRUD fields round-trip.
- BFF additions during UI work: CSRF-protected **`POST /bff/session/extend`** explicitly renews the cookie
  for eight hours. OIDC signout now returns `{redirectUrl}` so the browser can navigate to the IdP.
  Frontend logout failures are surfaced instead of silently clearing local state. Document these additions
  in `docs/admin-api.md` and add tests (last Admin API run still passed all 22).
- Actual Node is **24.12.0**; `npm ci` reported engine warnings for `abbrev@5` / `nopt@10`
  requiring Node 24.15+ (or supported alternatives). No global toolchain changes were made.
- Steps **4–9 have not started**. No AppHost edits, integration suite, Playwright suite,
  compose hardening or final documentation/verification yet.
- All repository files were untracked at the start. No commits were created and no existing work was reverted.

### Latest paused state
- Step 3 remains incomplete; steps 4–9 remain untouched.
- `@eslint/js` is now installed. The latest completed UI typecheck and production build passed;
  the domain regression suite passed all 71 tests.
- Subsequent, **unvalidated** edits adjust the accessible-label lint rule and AsyncState default,
  correct theme translation IDs and spend headings, preselect the wizard's team budget, refresh
  dependent organisation choices, and enlarge checkbox/radio targets.
- Added API, store and component test files (`client.spec.ts`, `stores.spec.ts`,
  `components.spec.ts`). These have **not been run**; resolve any compile/lint/test failures
  before treating them as coverage.
- The API contract now documents session extension and the JSON logout redirect.
- Resume with UI lint, typecheck and tests, then finish the remaining step-3 work above.

## Latest continuation checkpoint — 2026-10-07 (supersedes the old step-3 pause)

The user requested a fresh-agent handoff. **Steps 1–6 are complete.
Steps 7–9 have not started.** Do not use the historical pause notes above as current status.

### Verified results
- Domain: 71; gateway: 52; Admin API: **27**; integration: 6 passing tests.
- Admin UI: npm ci, lint with zero warnings, typecheck, 191 tests and production build passed.
- Real Aspire HTTPS stack starts healthy; fallback and PII rerouting verified.
- Four original real-Keycloak browser journeys passed: organisation/key creation, show-once
  acknowledgement, accounting, immediate rotation, role scoping, portal clipboard, session renewal,
  ops, logout and revocation.
- All six accessibility tests passed: 18 routed pages × four themes with available axe
  A/AA/AAA rules and 44px controls; 320px/200% text reflow; keyboard skip/focus and
  forced-colors/reduced-motion. This is automated coverage, **not WCAG AAA certification**.

### Changes since the previous checkpoint
- Fixed real OIDC logout in `src/Ume.LlmGateway.AdminApi/Program.cs`: JSON end-session
  responses must explicitly include client ID and protected AuthenticationProperties state.
  Handling the redirect event skips ASP.NET's default state assignment; without it, the
  signout callback ends on a blank page instead of returning to `/`.
- Added allowed signout callback URLs in `dev/keycloak/ume-realm.json`, static OIDC metadata
  in the Admin test fixture and a passing logout regression test. SaveTokens remains false.
  Updated `docs/admin-api.md` with callback registration and possible IdP confirmation.
- Browser harness now paces browser API requests and key-helper mutations at 600ms intervals
  to stay below the real 120 requests/minute limit. Earlier rapid scans hit 429; paced scans passed.
- **Latest edits verified in the full suite:** `ready()` now rejects AsyncState error screens;
  the first journey explicitly checks the rotated key still receives 402 from its inherited
  zero budget; a fifth journey tests 24-hour grace, both keys usable, predecessor revocation
  and replacement still usable.

### Exact runtime state at handoff
- Aspire was started using `aspire start --apphost apphost.cs --non-interactive --format Json`
  and is **still running**. `admin-ui` was healthy on `https://localhost:5173`.
- The latest command was moved to background by the user and subsequently finished successfully:
  `Set-Location tests\e2e; npx tsc --noEmit; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE };
  npx playwright test --max-failures 1`
  Tool shell ID **153**. Its completion output was read: **11 passed (5.9 minutes)**,
  including inherited-budget enforcement and 24-hour grace/revocation. Typecheck also passed.
- No further runtime operations were performed after the user's handoff request except reading
  that completed command. Aspire remains running. Stop the exact AppHost before backend/AppHost
  builds or edits.
- No commits have been created; all repository files were untracked initially. Preserve the worktree.

### Resume in this order
1. Read this handover, `docs/implementation-plan.md` and `docs/admin-api.md` in full.
2. Step 6 is verified complete; proceed to step 7. Rerun browser tests after relevant changes.
3. Step 7: load project-local Aspire deployment/wiring/lifecycle skills and use Aspire docs.
   Generate Compose through Aspire publish, then implement hardening: edge/data-internal/llm
   networks, no Redis/Postgres host ports, Redis ACL/TLS, Postgres TLS + migrator/app roles,
   nonroot/chiseled/read-only/dropped capabilities, secrets/certificates and production SPA assets.
4. Step 8: architecture, compliance/DPIA/processing-record inputs, accessibility statement and
   manual checklist, runbook, upgrades/ADRs/demo/README. Clearly distinguish verified controls
   from manual checks and legal/operator decisions.
5. Step 9: complete final verification. Address the known ops gap: health currently reports a
   hardcoded gateway version and does not expose live gateway health/usage-writer queue depth.
   Run all relevant suites, warning-free builds, dependency checks and the demo; stop cleanly.

### Test harness and operational cautions
- Playwright lives in `tests/e2e`; dependencies and Chromium are installed, single worker.
  It reads dev login credentials from the AppHost secret store (JSON has a UTF-8 BOM).
  No auth storage-state files, traces, screenshots or videos are written; test pages close
  before artifact collection to avoid show-once secret DOM snapshots.
- Do not log/save prompts, responses, key plaintext, provider credentials, raw environment
  snapshots or dashboard login tokens. Parse Aspire describe in memory and print safe fields only.
- File-based AppHosts cannot use DistributedApplicationTestingBuilder; the six integration
  tests intentionally use the approved Testcontainers fallback with real Kestrel FakeLlm.
- Local host-run APIs need ephemeral loopback DB/cache endpoints; Compose must have no host
  publication. Vite must prefer ADMINAPI_HTTPS over ADMINAPI_HTTP.
- Load matching skills before Aspire lifecycle/deployment work; project-local `.agents/skills`
  take precedence. Never use `dotnet run` to launch AppHost or build locked running resources.
- Overall task remains incomplete. Do not call task_complete until steps 7–9 are actually done.

### Current continuation — 2026-10-07
- Shell 153 was unavailable in this continuation. Step 6 was rerun: typecheck and all **11**
  browser/accessibility tests passed (5.9 minutes), including the latest stricter readiness,
  inherited-budget and grace-period journeys. Automated checks are not AAA certification.
- Exact AppHost stopped successfully before backend/AppHost changes.
- Step 7 is now implemented and live-verified as described above. Warnings-as-errors solution
  build and six integration regressions passed. Test Compose containers/volumes were cleaned up.
- Steps 8–9 remain in progress/not yet verified. No commits created; preserve the worktree.

## Final verified checkpoint — 2026-10-07 (supersedes earlier continuation/pause state)

**POC steps 1–9 are implemented and verified.** This does not authorize real municipal
data processing or certify WCAG AAA/production compliance. Read the release gates in
`docs/security-and-compliance.md` and the unperformed manual accessibility checklist.

### Final evidence
- Warnings-as-errors Release solution build: **0 warnings, 0 errors**.
- Full backend run: **169 passing** (domain 71, gateway 57, Admin API 35, integration 6),
  no failed/skipped tests.
- UI: lint with zero warnings, typecheck, **193 passing** tests and production build.
- Browser typecheck + full real-Aspire suite: **12 passing (6.1 minutes)**. Six accessibility
  tests cover page/theme axe rules, keyboard, reflow, forced-colors/reduced-motion; five
  original/grace user journeys plus a final synthetic demo verify fallback, PII on-prem,
  hard budget block, live writer status, theme switch, real OIDC roles/logout/rotation.
- Final immutable production images verified through generated Compose + mandatory override:
  Postgres/Redis TLS, authenticated namespace/command ACL, restricted DML roles and DDL
  rejection, append-only audit, health/readiness gating, nonroot/read-only/drop-all services,
  no DB/cache publication, SPA deep links/assets/fonts/JavaScript/CSP in Chromium,
  signed live gateway version/queue, binary backup and actual restore/restart.
- .NET direct/transitive dependency audit: **0 vulnerable packages**. UI/e2e npm audit:
  **0 vulnerabilities**. Three SPDX SBOMs generated/parsed in the session artifact directory
  `files/sbom/{Gateway,AdminApi,MigrationService}`. Dependency-audit/SBOM CI is provided;
  remote CI execution and municipal container-image scan/signing are not claimed.

### Meaningful final changes
- Ops now reads actual gateway health/version and writer queue/capacity/in-flight/last-write/
  failure count over HTTPS with a short-lived, purpose-separated HMAC signature. Unavailable
  probes explicitly show Unhealthy and null version/queue; no invented version or zero queue.
- Writer retains failed batches, back-pressures callers and retries idempotently instead of
  silently losing failed requeues. Outage/recovery and duplicate-work tests pass. The channel
  is still volatile across process/host loss; no durable-accounting guarantee is made.
- Production rejects insecure DB/Redis/key-ring configuration and HTTP upstream calls;
  provider redirects are not followed. Development/test HTTP compatibility remains.
- Dev Keycloak/FakeLlm/Vite are excluded from production. Ollama is local run-mode only:
  publishing with its flag enabled fails explicitly; production on-prem providers need an
  independently managed HTTPS endpoint. This avoids an unhardened HTTP-only container.
- Required architecture, compliance/DPIA/processing-record inputs, accessibility statement,
  runbook, backup/restore tools, upgrades, ADRs, demo, README and changelog are present.

### Exact final runtime/worktree state
- The exact `apphost.cs` was stopped cleanly after final browser verification.
- All disposable `ume-proof-*` test containers, networks, database volumes and held tmpfs
  secret volumes were removed. Temporary test backup archives were deleted.
- No prompt/response content, plaintext virtual keys/provider credentials, raw environment
  snapshots or dashboard login tokens were logged/saved. Test secrets remained memory-only;
  existing development secret stores were preserved.
- No commits created; the original untracked worktree was preserved. Generated base Compose
  is reproducible with `aspire publish --apphost apphost.cs -o deploy\generated --non-interactive`;
  **never deploy that base without `deploy/compose.hardening.yaml`.**

## AppHost project migration — 2026-10-07 (latest layout/runtime checkpoint)

- At the user's request, moved the root file-based AppHost into
  `src/Ume.LlmGateway.AppHost/Program.cs` with `Ume.LlmGateway.AppHost.csproj`,
  and added it under `/src/` in `UmeLlmGateway.slnx`. Open the solution and select
  `Ume.LlmGateway.AppHost` as the startup project.
- The SDK remains 13.6.0 with `AspireUseCliBundle=true`; hosting integration versions
  now use central package management. The SDK itself supplies the implicit
  `Aspire.Hosting.AppHost` package, which must not receive a central PackageVersion.
- Moved launch profiles to `Properties/launchSettings.json`, updated root
  `aspire.config.json`, and removed the obsolete root `apphost.cs`/`apphost.run.json`.
  Plain `aspire start`/`aspire publish` from the repository root select the new project.
- The resource graph, HTTPS ports, production exclusions and shared development
  secret-store ID (`ume-llm-gateway-apphost-dev`) are unchanged. Project resources
  now use typed SDK project references; Keycloak realm and Vite paths were rebased.
- Moving an AppHost changes generated Docker volume identity. Set the optional
  run-mode-only `DevelopmentVolumes:Postgres`/`DevelopmentVolumes:Ollama` user-secret
  settings to existing volume names when migrating an existing checkout. On this
  machine, PostgreSQL was configured to reuse `apphost-d91c2ba013-postgres-data`;
  its mounted reuse was verified. Existing secret values and data were preserved.
- Verification for this relocation: clean Release solution build (zero warnings/errors),
  root-discovered Compose publish, successful mandatory-hardening merge with nonsecret
  inputs, healthy gateway/admin UI and dependencies, and the targeted real-OIDC
  portal/session/operations/logout browser journey. The enabled-Ollama publish refusal
  still works. Earlier full-suite counts above are prior POC evidence, not reruns here.
- The exact new AppHost was stopped successfully after verification. No commits created.

## Routing rules (2026-10-09, latest)

Routing rules (Bifrost-style, in-house CEL-subset conditions) are implemented end to end: engine (`Domain/Routing`), gateway (`RouteResolver`, `x-ume-rule`), storage (migrations `RoutingRules`,
`RoutingRuleOnUsage`), admin API (`/api/routing-rules`), admin UI (Routing > Routing rules) and docs. Read `docs/routing-rules.md` (concepts, language, API/UI), `docs/routing-rules-plan.md`
(delivery checklist and design decisions) and ADR 013-014. Test counts when finished: domain 215, gateway 96, admin API 62, integration 7, UI 121.

**Open work, with a checklist and a findings log: `docs/handover-ui-verification.md`** (full-stack check incl. production-shaped DB roles, manual screen reader pass, rewrite of the stale Playwright e2e tests).
The Status table above predates this: the Playwright row ("12 passing") is out of date, and the admin UI row says Tailwind 3 (it is Tailwind v4).

## Frontend rebuild and model discovery � 2026-10-08 (latest)

- **Frontend** rebuilt from the design prototype; see README "Admin UI". Known gaps: route form lost its up/down reorder buttons, Ops page has no provider drain/resume action, e2e specs outdated, unit/component specs to rewrite, themes/mobile need a browser check.
- **Start scripts:** `scripts/Start-Dev.ps1` (demo data by default, `-Empty` for an admin-only environment via `Seed__Enabled=false`). The AppHost seeds only in run mode when `Seed:Enabled` is true. Not yet verified with a full AppHost build.
- **Model discovery:** `POST /api/providers/{id}/discover-models` (`AdminApi/ModelDiscovery.cs`) lists upstream models, doubling as a connection test. Ollama uses `/api/tags` + `/api/show`; Azure OpenAI is unsupported. Dialog: `components/providers/DiscoverModelsDialog.vue`.
- **Model capabilities:** new `ModelDeployment.Features` (`text[]`), migration `ModelFeatures`, exposed as `features` on the model API, shown in the Models table and editable in the model drawer.
- Not yet covered by automated tests: discovery (needs a fake `HttpMessageHandler` test) and the dialog.
- Prices are not available from Ollama, OpenAI or Anthropic APIs; add them manually.
