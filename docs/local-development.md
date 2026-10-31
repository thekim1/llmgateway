# Local development

Windows: .NET SDK 10.0.403, Aspire 13.6, Docker 28+, Node 24.15+ recommended.
Use PowerShell 5.1 and run commands separately (or chain with `;` and exit-code checks).

```powershell
Set-Location src\admin-ui
npm ci
Set-Location ..\..
.\scripts\Start-Dev.ps1          # with demo data (default)
.\scripts\Start-Dev.ps1 -Empty   # empty database, only the Keycloak login users
```

The script wraps `aspire start` + `aspire wait admin-ui`; you can still run those directly
(demo data is then on unless `Seed__Enabled=false` is set in the environment).

## Demo data or an empty environment

Seeding (`Seed:Enabled`) runs in the migration service and **only when the database has no
departments**. Demo data is fictional förvaltningar, teams, providers, models, prices, routes and
a dev key and three example routing rules (see [Routing rules](routing-rules.md)). In the empty variant you sign in with a Keycloak test user (`gateway-admin`) and create
everything yourself. To switch an existing local environment, stop the stack, delete the
AppHost's Postgres Docker volume (`docker volume ls`, then `docker volume rm <name>`; or the
volume named in `DevelopmentVolumes:Postgres`) and start again.

## Fake LLM toggle

The fake provider (`fake-llm`) starts with the AppHost by default. Set `"FakeLlm:Enabled": false`
in the AppHost user secrets (or `FakeLlm__Enabled=false` in the environment) to skip it. The admin UI
badge shows **Development** or **Production** from the admin API's environment.

By default the fake LLM answers instantly. To make chat, responses and messages answers behave like a real provider,
set `FakeLlm:Latency` in the AppHost user secrets (or `FakeLlm__Latency__*` on the fake LLM itself). All values
default to 0 (off):

| Setting | Effect | Example |
|---|---|---|
| `FirstTokenMs` | Wait before the first token (non-streamed answers wait for the whole answer) | `400` |
| `PrefillMsPer1kInputTokens` | Extra wait before the first token per 1 000 input tokens (long prompts take longer to read) | `60` |
| `TokenIntervalMs` | Wait between streamed words | `25` (about 40 words/s) |
| `JitterPercent` | Random ± variation on every wait | `30` |
| `AnswerWords` | Pad answers to this many words | `200` |
| `StallProbability`, `StallMs` | Chance per word of an extra pause, and its length (a content filter releasing a batch) | `0.01`, `1500` |

With `stream_options.include_usage`, the fake LLM's chat chunks carry `"usage":null` as OpenAI's do.

## Running from Visual Studio

1. Install Visual Studio 2022 17.14+ (or newer) with the .NET 10 SDK, Docker Desktop (running), Node 24+, and run `dotnet dev-certs https --trust` once.
2. Open `UmeLlmGateway.slnx`, set `Ume.LlmGateway.AppHost` as startup project, profile **https**.
3. Right-click the AppHost project > **Manage User Secrets** and paste the dev secrets JSON
   (`Parameters:*` for pepper, dev-key, OIDC secret, dev user password, Redis and **Postgres**
   passwords, plus `DevelopmentVolumes:Postgres`). Keep them stable: the Postgres volume stores the
   password it was created with, and the key pepper hashes all virtual keys, so changing either
   later breaks the existing data. With these set, data survives restarts and `git pull`
   (migrations run automatically). Aspire generates the passwords itself if you skip this step.
4. `"Seed:Enabled": false` = empty environment; `true` or omitted = demo data. It only takes effect on
   an empty database (delete the Postgres Docker volume to switch).
5. Press F5. The Aspire dashboard opens; click the `admin-ui` endpoint. Keycloak test users are
   created from `dev/keycloak`; the shared dev password is the `dev-user-password` secret.

## Environments (dev / test / production)

There is no separate "test" configuration; the mode decides the environment:

| Environment | How | Identity | Data |
|---|---|---|---|
| Development | `aspire start` / `Start-Dev.ps1` (run mode, `Development`) | Keycloak test realm (optional: set `Oidc:Authority` in AppHost user secrets to use an existing provider), FakeLlm, optional Ollama | Demo or empty, see above |
| Test (production-shaped) | `.\deploy\Test-Deployment.ps1` (isolated, throw-away) | Test-only secrets | Empty, never seeded |
| Production | `deploy/compose.prod.yaml` after `deploy/init-deployment.sh` (`ASPNETCORE_ENVIRONMENT=Production`) | External HTTPS OIDC | Never seeded (`Seed__Enabled=false`); no Keycloak or FakeLlm |

Production inputs (OIDC authority/client, certificates, secrets, provider endpoints) are
generated or prompted for by `deploy/init-deployment.sh` (see *Quick production setup* below) and
described in [the runbook](runbook.md). Other settings can be overridden
with `Section__Key` environment variables (for example `Ollama__Enabled=true`, development only).

## Admin UI code

`src/admin-ui` is Vue 3 + Pinia + Vue Router with Reka UI primitives and Tailwind CSS v4, built from the design prototype in
`Design prototype/` (tokens in `src/admin-ui/src/styles/tokens.css`, Tailwind config in
`tailwind.config.cjs`). The UI is English only, with light, dark and Lumen themes
(`data-theme`, remembered in `localStorage`). Reusable building blocks live in
`src/components/ui` (buttons, tables, drawers, dialogs, form fields, meters), the page shell in
`src/components/layout`, and pages in `src/views`. Icons are a subset of Material Symbols; after
adding an icon name run `python src/admin-ui/scripts/subset-icons.py` (it needs the Python package `fonttools[woff]`).

## Build and test

The UI is `https://localhost:5173`. Local Keycloak test users/roles are provisioned by
the realm import; get development passwords through Aspire's secret store without
printing them, enabling transcripts or saving browser auth state. Ollama is opt-in:
set `Ollama__Enabled=true` before starting. FakeLlm is the default test provider.
Stop **this exact AppHost** before backend/AppHost edits or builds:

```powershell
aspire stop --apphost src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj --non-interactive
dotnet build UmeLlmGateway.slnx -v q -nologo
dotnet test --solution UmeLlmGateway.slnx
Set-Location src\admin-ui
npm run lint
npm run typecheck
npm test
```

`npm run lint` fails on warnings, exactly like CI (`npm run lint:fix` fixes most). To catch it before
pushing, enable the pre-commit hook once per clone: `git config core.hooksPath .githooks`.
Debug builds of the admin API (Visual Studio, F5) also run the lint and show problems as a build warning
(skipped when `node_modules` is missing or `CI=true`).

### Tests that do not flake

Each backend test project shares one fixture (Postgres, the gateway or admin API host, the mock provider) across all
its test classes, and xUnit runs the classes in parallel. A test that passes alone can therefore fail in CI, where two
CPUs make every race wider. The rules that keep them stable:

- **Make your own data.** Create the key, team or route the test needs, and find your requests by a unique marker
  (a `Guid` in the prompt), never by "the latest" or "the only" row. Do not assert on global state another class may
  change (settings, global routing rules); if a test must change it, restore it in `finally`.
- **Wait for asynchronous effects, do not sleep.** The mock provider logs a request only after answering it: read it
  with `fixture.UpstreamRequestsAsync` / `UpstreamBodyAsync`, never `Upstream.LogEntries`. Usage records:
  `fixture.UsageAsync` (flushes the writer). Changes that reach the gateway over Redis (revocation, rotation, budgets):
  retry the request until it has the expected status, as `WaitForStatusAsync` does.
- **Stay inside one time window.** Rate limits count per calendar minute and budget periods start at midnight in
  Stockholm. Call `TestClock.InOneRateLimitMinuteAsync()` before a test that needs several requests in the same minute
  (the fixtures already keep every test away from midnight). Use a `FakeTimeProvider` where the code takes one.

### CI and Actions minutes

The GitHub Actions minutes of the plan are shared by every developer and repository, and every job is billed rounded up
to a whole minute, so CI runs each check once per change:

| Workflow | Runs on | Not on |
|---|---|---|
| *Verify POC* (about 3 min) | ready pull requests (their merge with `main`), direct pushes to `main`, release tags | draft pull requests; squash merges of a pull request (subject ends in `(#123)`, already verified on the pull request); changes to Markdown, `docs/`, `site/`, `benchmarks/compare/` or the design prototype only |
| *Deploy end to end* (about 6 min) | ready pull requests that change `deploy/` or ServiceDefaults | drafts |
| *Website* (1 min) | pushes to `main` that change the site, `docs/` or the CHANGELOG (build and publish in one job); pull requests that change `site/` | drafts; pull requests that only change `docs/` (a broken link fails the build on `main`) |
| *Release* | `v*.*.*` tags: verifies the tag again, then builds and publishes | |
| *Flake hunt* | Mondays, or by hand (also scans the base image for new CVEs) | |

Within *Verify POC*, the base image scan runs on pull requests only when they change `Directory.Build.targets`, and the
SPDX SBOM is only generated for releases. A newer push cancels the run in progress, also on `main`.

Habits that keep it that way:

- **Run the checks before pushing:** `.\scripts\Test-BeforePush.ps1` runs what *Verify POC* runs: a Release build
  with warnings and unnecessary usings as errors, the tests, the vulnerability audits and the admin UI checks
  (`-Quick` skips the backend tests, `-NoUi` the UI). It warns when your .NET SDK is not the one CI uses
  (`dotnet-version` in the workflows): another SDK reports other unnecessary usings, and a build that passes locally
  then fails in CI.
- **Open pull requests as drafts** and push as often as you like; mark the pull request *Ready for review* when it is
  done, which runs CI once. Push follow-up fixes together rather than one commit at a time: a newer push cancels the
  run in progress, but the minutes already used are billed.
- **Merge with squash**, so the merge commit on `main` is not built again.
- **`[skip ci]`** in a commit message skips every workflow for that push (for example a typo fix pushed straight to
  `main`); the next release verifies it.

A branch without a pull request is not verified automatically: open a draft pull request and mark it ready, or run
*Verify POC* from the Actions tab on the branch.

To hunt for flakes, run the workflow *Flake hunt* (Actions tab): it runs the backend tests on a CI runner several times
and lists every test that failed. It also runs once a week.

**Known flakes stay out of the pull request pipeline.** A test that has failed intermittently because of timing or a race
is marked `[Trait(TestCategory.Name, TestCategory.Flaky)]` (`tests/Ume.LlmGateway.TestKit/TestCategory.cs`) with a comment
saying why. *Verify POC* runs `dotnet test ... --filter-not-trait "Category=Flaky"`; locally `dotnet test` runs every
test, and *Flake hunt* runs the marked ones too, so they are still watched. Run only the pipeline's set with
`dotnet test --solution UmeLlmGateway.slnx --filter-not-trait "Category=Flaky"`, or only the flaky ones with
`--filter-trait "Category=Flaky"`. The trait is a to-do, not a home: fix the test and remove it. Marked now:

| Test | Why |
|---|---|
| `RealtimeTests.A_session_is_closed_when_its_budget_runs_out` | Timing of the periodic budget check (2 of 5 flake-hunt runs, 2026-10-19) |
| `AudioTests.Pii_policy_redacts_the_prompt_field` | Reads the shared mock provider's log (failed in CI 2026-10-13 and 2026-10-16) |
| `HotPathTests.Expired_key_is_served_while_it_refreshes_and_revocation_drops_it` | Races the background key refresh (CI, 2026-10-15) |
| `GatewayTests.Requests_in_flight_are_limited_per_key` | Needs a slow request still in flight 300 ms later on a busy runner |
| Admin UI `RuleFormDrawer` "does not save while the condition is known to be invalid" | The submit can run before the debounced validation result is applied (Vitest: `it.skipIf(!!import.meta.env.CI)`) |

## Browser tests

Browser tests (`tests\e2e`, Playwright) need a running stack. Use the separate test database so your
development data is never touched:

```powershell
.\scripts\Start-E2E.ps1          # own Docker volume (ume-e2e-postgres), reset on every start, demo data, fake LLM on
Set-Location tests\e2e
npm ci
npx playwright install chromium
npx tsc --noEmit
npx playwright test
Set-Location ..\..
.\scripts\Start-E2E.ps1 -Stop    # stops the stack and removes the test database
```

The test stack uses the same ports as the dev stack, so stop the dev stack first. The tests cover sign-in, keys
(show-once secret, rotation, revocation), organisation, usage, audit, budgets, the demo scenario, routing
rules (create, validate, test, reorder, disable, delete, team and department deletion and reassignment, key rotation, `budget_used`/`tokens_used`, with real gateway
requests) and accessibility (axe at WCAG 2.2 AA in the three themes, drawers and dialogs, reflow, keyboard,
forced colours, target size). They do not save screenshots, videos, traces or storage state.
No manual screen reader testing is planned; open follow-ups are listed in [HANDOVER.md](../HANDOVER.md).

To regenerate the routing screenshots in `docs/images` after a UI change, run
`npx playwright test -c playwright.docs.config.ts screenshots` in `tests\e2e` against the test stack. The script builds the
two routing examples through the UI, so it changes the (disposable) test database only.

## Website

`site/` builds the public website (<https://thekim1.github.io/llmgateway/>): the product page, Getting started, Benchmarks
and every documentation page listed in `site/content.mjs`, rendered from the Markdown files in `docs/`. The workflow
`.github/workflows/pages.yml` builds it on every pull request that touches the site or the docs, and publishes it to
GitHub Pages on `main`. To add a page, write the Markdown file in `docs/` and add it to `DOCS` in `site/content.mjs`.

```powershell
Set-Location site
npm ci
npm run serve        # builds into site/dist and serves http://localhost:4321/
```

The build fails on a link to a page, file or heading that does not exist. Links between Markdown files and to `docs/images`
and `docs/reports` become site links; links to other repository files go to GitHub.

The admin UI pictures on the website (`site/public/screenshots`, one per theme) come from the real UI. To regenerate them,
start the test stack and run `npx playwright test -c playwright.docs.config.ts website` in `tests\e2e`. Only demo data
appears; the secret in the key dialog is masked and the keys the script creates are revoked.
