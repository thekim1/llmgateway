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

1. **Manual screen reader pass (needs a person).** Nothing has been listened to. Use NVDA with Firefox or Chrome (and VoiceOver with Safari if available)
   in all three themes, at 200 % zoom and 320 px. Automated axe, focus, heading and name checks already pass. Things worth special attention:
   `<datalist>` model-name inputs (replace with a Reka UI combobox if suggestions are not announced), the live regions (condition status, "now checked N of M"
   after moving a rule, inline alerts), focus after opening and closing drawers and dialogs, the segmented controls, the `<details>` disclosures, `aria-disabled` move buttons,
   and `lang="sv"` text. Record tool, browser and version in `docs/accessibility-statement.md`, which must not claim a screen reader pass until then.
2. **Decide:** a duplicate rule name (409) is shown as a form-level message, not on the *Name* field.
3. **Decide:** the "Skip to main content" link is hidden from 768 px (`md:hidden`); the sidebar has about 15 tab stops before `main`. Landmarks are sufficient, but a skip link is cheap.
4. **Fix:** `SettingsView` says theme options are in the top bar, but the theme control is in the sidebar and only visible from 1024 px.
5. **Test gap:** model discovery (`AdminApi/ModelDiscovery.cs`, `DiscoverModelsDialog.vue`) has no automated tests (needs a fake `HttpMessageHandler`).
6. Prices are not published by Ollama, OpenAI or Anthropic; they are entered manually.
7. **Performance, not done yet:** the Anthropic *translated* stream (`/v1/chat/completions` to an Anthropic provider) still builds a JSON tree per event
   (about 1.8 MB per 300-chunk stream); rewriting `AnthropicStreamTranslator` with `Utf8JsonWriter` would remove most of it. See [docs/performance.md](docs/performance.md).

## Conventions and gotchas

- **Toolchain:** .NET SDK 10.0.401, Aspire CLI 13.6, Node 24, Docker. Windows/PowerShell 5.1 is the primary dev environment (use `;`, not `&&`); WSL works for most things.
- **UI under WSL:** `src/admin-ui/node_modules` holds Windows binaries, so `npm test` / `npm run build` fail in WSL. Copy the UI without `node_modules` to a scratch directory, `npm ci` there and
  `rsync -a --delete --exclude node_modules --exclude dist <repo>/src/admin-ui/ <scratch>/ui/` before each run. Never run `npm ci` in the repo copy. Gate: `npm run lint -- --max-warnings 0`, `npm run typecheck`, `npm test`, `npm run build`.
- **Tests:** `dotnet test --solution UmeLlmGateway.slnx` (Microsoft Testing Platform, set in `global.json`); xUnit v3 + Shouldly. Do not assert on the whole content of a shared table or list in
  AdminApi/Gateway tests; assert on your own rows (the AdminApi tests run serially because they share one database).
- **Hot path:** the request handler, adapters, stores and caches are performance-critical. Run the benchmarks (`-- --filter *Pipeline*`, `*Provider*`) and the `audit` before and after changing them,
  and keep "no Postgres on the request path" and the Redis round-trip budget in [docs/performance.md](docs/performance.md) true.
- **Analyzers:** warnings are errors (`AnalysisLevel latest-recommended`, some CA rules in `NoWarn`). Use `[LoggerMessage]` logging.
- **AppHost:** `src/Ume.LlmGateway.AppHost`. Stop the exact AppHost (`aspire stop --apphost ...`) before backend builds. File-based AppHosts cannot use `DistributedApplicationTestingBuilder`, hence Testcontainers in the integration tests.
  Never use `dotnet run` for the AppHost. Project-local skills are in `.agents/skills`.
- **Dev secrets:** from the Aspire user-secret store; never print them or save browser auth state. This machine's secrets set `Seed:Enabled=False` and `FakeLlm:Enabled=False`; `scripts/Start-E2E.ps1` overrides both.
- **Moving the AppHost changes the Docker volume identity:** set `DevelopmentVolumes:Postgres` (and `Ollama`) in user secrets to reuse existing data (see README).
- **Compose:** never deploy `deploy/generated/docker-compose.yaml` without `deploy/compose.hardening.yaml`.
- **Playwright:** artifacts (trace, screenshot, video) are off on purpose so show-once keys cannot be captured; the helper paces admin requests to stay under the 120 requests/minute limit.
- **Packages:** central package management in `Directory.Packages.props`. Keycloak hosting exists only as a preview package. The AppHost SDK supplies `Aspire.Hosting.AppHost` implicitly (no PackageVersion for it).
  Testcontainers: use `new PostgreSqlBuilder("postgres:17-alpine")`.
- **C# parser quirk:** `case "x" when a?["b"] is { } y:` fails to parse; use if/else.
- **Icons:** after adding an icon name run `python src/admin-ui/scripts/subset-icons.py` (needs `fonttools[woff]`).
- **Do not log or save** prompts, responses, key plaintext, provider credentials or raw environment snapshots.
