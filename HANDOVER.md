# Handover: Umeå kommun LLM Gateway POC

Current state for a fresh agent or developer. History lives in git and [CHANGELOG.md](CHANGELOG.md); the design is in
[docs/architecture.md](docs/architecture.md) and [docs/adr.md](docs/adr.md); the API contract is [docs/admin-api.md](docs/admin-api.md).

## Status

Everything in the original POC scope and the routing rules feature is implemented, tested and documented.

| Area | State |
|---|---|
| Gateway (data plane), Admin API (BFF, RBAC, CRUD, reports, ops, config import/export, audit), MigrationService, FakeLlm | Done |
| Routing rules (engine, gateway, admin API, admin UI, usage visibility) | Done, see [docs/routing-rules.md](docs/routing-rules.md) |
| Admin UI (`src/admin-ui`) | Done: Vue 3, Pinia, Reka UI, Tailwind v4, English, light/dark/Lumen |
| Aspire AppHost, Compose publish with mandatory hardening, backup/restore | Done; `deploy/Test-Deployment.ps1` passes |
| Data-plane performance | Benchmarked and optimised, see [docs/performance.md](docs/performance.md) (`benchmarks/Ume.LlmGateway.Benchmarks`) |
| Tests | Domain, gateway, admin API, integration (Testcontainers), UI (Vitest) and Playwright e2e (34 tests, own test database via `scripts/Start-E2E.ps1`) all pass |

The POC is not a production approval, legal attestation or WCAG certification (see the release gates in
[docs/security-and-compliance.md](docs/security-and-compliance.md)).

## Open items

1. **Screen reader pass: not planned.** Decided 2026-10-10: there are no resources for manual testing with NVDA or VoiceOver. The automated axe,
   focus, heading and name checks remain the evidence. `docs/accessibility-statement.md` keeps saying that no screen reader has been run; an adopter
   who needs that evidence (for example before public use) has to arrange it. Untested areas most likely to need it: `<datalist>` model-name inputs, the
   live regions, focus after drawers and dialogs, the segmented controls, the `<details>` disclosures and `aria-disabled` move buttons.
2. **Decide:** a duplicate rule name (409) is shown as a form-level message, not on the *Name* field.
3. **Decide:** the "Skip to main content" link is hidden from 768 px (`md:hidden`); the sidebar has about 15 tab stops before `main`. Landmarks are sufficient, but a skip link is cheap.
4. **Test gap:** `DiscoverModelsDialog.vue` has no automated UI test (the backend `ModelDiscovery` is covered by `ModelDiscoveryTests`).
5. Prices are not published by Ollama, OpenAI or Anthropic; they are entered manually.
6. **Performance work list:** [docs/performance-improvement-plan.md](docs/performance-improvement-plan.md) (start there). **Current numbers:** the baseline of 2026-10-27 in
   [docs/performance.md](docs/performance.md#results-baseline-2026-10-27) (17 888 streamed messages/s on 4 vCPUs,
   close to linear from 1 to 4 vCPUs, the gateway's own CPU the limit). Re-measure everything in one session (see
   *Measuring* there) and update the baseline, the website (`site/content.mjs`), the platform comparison slides and the
   other docs from it, so numbers never come from mixed runs. Do not keep older figures anywhere.

## Conventions and gotchas

- **Toolchain:** .NET SDK 10.0.401, Aspire CLI 13.6, Node 24, Docker. Windows/PowerShell 5.1 is the primary dev environment (use `;`, not `&&`); WSL works for most things.
- **UI under WSL:** `src/admin-ui/node_modules` holds Windows binaries, so `npm test` / `npm run build` fail in WSL. Copy the UI without `node_modules` to a scratch directory, `npm ci` there and
  `rsync -a --delete --exclude node_modules --exclude dist <repo>/src/admin-ui/ <scratch>/ui/` before each run. Never run `npm ci` in the repo copy. Gate: `npm run lint -- --max-warnings 0`, `npm run typecheck`, `npm test`, `npm run build`.
- **Tests:** `dotnet test --solution UmeLlmGateway.slnx` (Microsoft Testing Platform, set in `global.json`); xUnit v3 + Shouldly. Do not assert on the whole content of a shared table or list in
  AdminApi/Gateway tests; assert on your own rows (the AdminApi tests run serially because they share one database).
- **Hot path:** the request handler, adapters, stores and caches are performance-critical. Run the benchmarks (`-- --filter *Pipeline*`, `*Provider*`) and the `audit` before and after changing them,
  and keep "no Postgres on the request path" and the Redis round-trip budget in [docs/performance.md](docs/performance.md) true.
- **Where things live** (see *Code layout* in [docs/architecture.md](docs/architecture.md)):
  - Endpoint semantics (routing name, paths, capability, model kinds, audio/realtime/streaming flags) only in `GatewayEndpoints` (Domain); do not add endpoint switches.
  - Redis keys and channels only in `RedisKeys` (all under `ume:`; the ACL in `deploy/redis/users.acl.example` depends on it).
  - Key rotation lineage and budget spend queries in `GatewayQueries` (Infrastructure), shared by gateway and admin API.
  - Data retention (ADR 016): usage before `GatewaySettings.UsageRetainedFrom` exists only in `UsageMonthlyTotals`. New code that sums
    usage must read totals before that boundary and records from it on (`GatewayQueries.CostsIn`, `ReportEndpoints.Facts`, the Data API
    aggregate SQL). Only the database function `ume_retention_step` deletes usage, refused-key, alert or audit rows; change it in a new
    migration (`CREATE OR REPLACE`), never by granting `DELETE` to an app role.
  - Gateway-wide settings live in the single `GatewaySettings` row; the gateway reads it with the catalogue (trusted proxies), so a
    change the gateway must see publishes `InvalidationKind.Config`.
  - Provider failures carry a `FailureKind`: only `Transient` counts toward the circuit breaker; `ClientError` is answered without fallback.
  - Every accepted gateway request is accounted exactly once (`AccountAsync`); new failure paths must go through it.
  - Admin API endpoints stay thin; logic belongs in the services (`RoutingRuleService`, `ProviderCatalogService`, `BudgetAdminService`,
    `ConfigTransferService`, `OwnerScopeResolver`). `AdminContext` carries the signed-in user (`ctx.Keys()`, `ctx.Teams()`, `ctx.Actor`, `ctx.SaveAsync(...)`):
    never pass a `ClaimsPrincipal` around. Throw `ApiFaultException` (English message) for client errors; audit with the snapshot records in `AuditSnapshots.cs`, never entities.
  - Gateway refusals are a `GatewayError` from the catalogue in `GatewayErrors.cs` (status, code, English message); add new ones there, not inline.
  - A new gateway endpoint is one entry in `GatewayEndpoints` (Domain) plus its OpenAPI summary in `GatewayRoutes`; uploads go through
    `MultipartFormReader` with their own `MultipartRules`; provider adapters read complete answers with `ReadCompleteAsync`.
  - Every cost, budget, spend counter and reservation is in USD; there is no other currency (SEK was removed in 0.8.1). The `CostSek` columns
    and `ExchangeRates` table are left over for one release (rolling update) and dropped in the next; nothing reads or writes them.
  - Web hosts use the ServiceDefaults helpers (`AddUmeWebDefaults`/`UseUmeWebDefaults`, `UseUmeSecurityHeaders`, `ExitIfHealthProbeAsync`); CSV cells go through `CsvCell` (Domain).
  - Options are validated on start (DataAnnotations/`IValidatableObject`); add a range or `[Required]` with each new setting.
- **Analyzers:** warnings are errors (`AnalysisLevel latest-recommended`, some CA rules in `NoWarn`). Use `[LoggerMessage]` logging.
- **AppHost:** `src/Ume.LlmGateway.AppHost`. Stop the exact AppHost (`aspire stop --apphost ...`) before backend builds. File-based AppHosts cannot use `DistributedApplicationTestingBuilder`, hence Testcontainers in the integration tests.
  Never use `dotnet run` for the AppHost. Project-local skills are in `.agents/skills`.
- **Dev secrets:** from the Aspire user-secret store; never print them or save browser auth state. This machine's secrets set `Seed:Enabled=False` and `FakeLlm:Enabled=False`; `scripts/Start-E2E.ps1` overrides both.
- **Moving the AppHost changes the Docker volume identity:** set `DevelopmentVolumes:Postgres` (and `Ollama`) in user secrets to reuse existing data (see README).
- **Compose:** never deploy `deploy/generated/docker-compose.yaml` without `deploy/compose.hardening.yaml`.
- **Playwright:** artifacts (trace, screenshot, video) are off on purpose so show-once keys cannot be captured; the helper paces admin requests to stay under the 120 requests/minute limit.
- **Packages:** central package management in `Directory.Packages.props`. Keycloak hosting exists only as a preview package. The AppHost SDK supplies `Aspire.Hosting.AppHost` implicitly (no PackageVersion for it).
  Testcontainers: use `TestPostgres` from `tests/Ume.LlmGateway.TestKit` (image constant, migrate before the host starts); shared test auth, XSRF clients and `CapturingLoggerProvider` live there too.
- **Container images:** the pinned .NET base image digest and container user are in `Directory.Build.targets`; a service opts in with `<UmeServiceContainer>true</UmeServiceContainer>`.
- **C# parser quirk:** `case "x" when a?["b"] is { } y:` fails to parse; use if/else.
- **Icons:** after adding an icon name run `python src/admin-ui/scripts/subset-icons.py` (needs `fonttools[woff]`).
- **Do not log or save** prompts, responses, key plaintext, provider credentials or raw environment snapshots.
