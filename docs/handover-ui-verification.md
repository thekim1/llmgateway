# Handover: verify the routing rules UI on the full stack, run a screen reader pass, update the e2e tests

**Status (2026-10-09): Task 3 (e2e rewrite) and Task 1 (full stack and production-shaped deploy) are done; Task 2 (manual screen reader pass) is open, only its automated part is done; the four accessibility findings from the new e2e run are fixed and the suite is green (17 of 17).** Written at the end of routing rules step 9. A fresh agent can pick this up with no prior context. For a stack to test against use `scripts/Start-E2E.ps1` (separate, disposable test database).
Append anything new you find to the **Findings log** at the bottom (keep it current; do not leave findings only in chat).

## Why this exists

The routing rules feature (see [routing-rules.md](routing-rules.md) and [routing-rules-plan.md](routing-rules-plan.md)) was built and tested
with unit, component, API and integration tests (all green when this was written), and its UI was checked visually in light, dark and lumen and at
phone width, but **only against a stand-in API**. Three things were deliberately left undone and are the job here:

1. **Check everything on the full stack** (real Aspire stack: Keycloak login, Postgres, Redis, gateway, admin API, admin UI).
2. **Screen reader pass** (manual) plus an axe scan of the new page, in all three themes.
3. **Update the Playwright e2e tests** (`tests/e2e`) to the current design. They target an older UI and do not run against the current one.

Do not start by changing product code. First verify, record findings, then fix, then re-verify.

## What you need to know about the project

- Repo root: `ume-llm-gateway` (.NET 10 backend, Vue 3 admin UI). Windows is the primary dev OS (PowerShell 5.1: use `;` not `&&`); the work so far was done from WSL.
  Read `README.md` ("Local development", "Admin UI", "Routing rules page") and `HANDOVER.md` (conventions and gotchas) first.
- **Admin UI = `src/admin-ui`: Vue 3 + Pinia + Vue Router, Reka UI headless primitives, Tailwind CSS v4** (colours are tokens from
  `tailwind.config.cjs` / `src/styles/tokens.css`; never hard-code colours), Vite, Vitest + Vue Test Utils. English UI text; Swedish server messages are marked `lang="sv"`.
  Three themes: `light`, `dark`, `lumen` (`<html data-theme>`, stored in `localStorage` key `ume-theme`).
  Shared components: `src/components/ui`. Pages: `src/views`. Routing rules UI: `src/views/RoutingRulesView.vue`, `src/components/rules/*`,
  `src/stores/routingRules.ts`, `src/utils/routingRules.ts`.
- Routing rules backend: `src/Ume.LlmGateway.Domain/Routing/` (engine), `src/Ume.LlmGateway.Gateway/Pipeline/RouteResolver.cs` and `GatewayRequestHandler.cs`
  (applied to live requests), `src/Ume.LlmGateway.AdminApi/RoutingRuleEndpoints.cs` (admin API), migrations `RoutingRules` and `RoutingRuleOnUsage`.
  API contract: `docs/admin-api.md` ("Routing rules").
- **Demo data** (`Seed:Enabled`, default in dev) contains three global rules: *Äldre alias gpt-4* (chain), *Premium via header* (`x-ume-tier: premium`), *Budgeten nästan slut*.
- The `PreToolUse` hook in this environment blocks `rm -rf`; use other means to clean up.

### Running the UI unit tests (WSL gotcha)

`src/admin-ui/node_modules` was installed on Windows and contains Windows-only native binaries, so `npm test` / `npm run build` fail under WSL
("Cannot find native binding"). On Windows they work as normal. Under WSL, copy the UI (without `node_modules`) to a scratch directory, run `npm ci` there,
and sync before each run (`rsync -a --delete --exclude node_modules --exclude dist <repo>/src/admin-ui/ <scratch>/ui/`). Do not run `npm ci` in the repo copy.
The gate is: `npm run lint -- --max-warnings 0`, `npm run typecheck`, `npm test`, `npm run build`.
State when written: 121 UI tests, 215 domain, 96 gateway, 62 admin API, 7 integration tests, all passing.

## Task 1: check on the full stack

**Done 2026-10-09.** Every box below is ticked. Evidence: the dev/test stack on `scripts/Start-E2E.ps1` (fresh database, Keycloak login, real gateway and Redis), `deploy/Test-Deployment.ps1` (production-shaped, separate DB roles, backup and restore) and the Playwright specs `routingRules*.spec.ts` (34 of 34 e2e tests pass). Details of what was seen are in the Findings log. The in-place upgrade was checked on a throwaway copy of the developer's pre-feature database: migrations `RoutingRules` and `RoutingRuleOnUsage` were applied on top of `InitialCreate` ... `KeyAllowedProviders`, and the existing departments and keys were intact. The `tokens_used` and `budget_used` rules were checked with a key that has a limit and one that has not (the value exists only for the first), not with a minute window filling up.

Start the stack as the README describes (`.\scripts\Start-Dev.ps1`, demo data on; stop the exact AppHost before backend builds: see README). Sign in through Keycloak as
`gateway-admin`. Development passwords come from the Aspire secret store (never print them or save browser auth state; see README).

Verify, and write down what you actually saw (not what you expect):

**Migrations and data**
- [x] A fresh database migrates cleanly (`RoutingRules`, `RoutingRuleOnUsage`) and the demo rules are seeded.
- [x] An existing database from before this feature upgrades in place (additive migrations only).
- [x] Production-shaped run: `.\deploy\Test-Deployment.ps1`. **Check the role grants**: `deploy/postgres/app-roles.sql` now lets `ume_admin` write
      `"RoutingRules"` and `"RoutingRuleTargets"`. Creating/editing/deleting a rule as the admin API and reading rules as the gateway must work with the separate DB roles
      (this is the most likely place for a failure that no test so far could catch, because tests use a superuser).

**Admin UI on the real API** (Routing > Routing rules)
- [x] List and detail render the seeded rules; scope filter and counts are right.
- [x] New rule: live condition validation (valid, invalid with position, unreachable server), variables/examples helpers, targets with weights and share percentages, fallbacks, Chain, Enabled; save, edit, delete.
- [x] Server-side errors map onto the right fields (duplicate name -> 409, unknown model -> `targets`, bad condition -> `condition`, scope without owner -> `scopeId`).
- [x] Enable/disable, move earlier/later (the order really changes the gateway's behaviour), change owner / scope.
- [x] **Test a request**: with headers, parameters, key/team, usage values; true / false / could-not-evaluate shown; "does not exist" warnings; chain steps.
- [x] **Delete a team with rules** (Organisation): the 409 opens the question; *Deactivate* keeps the rules and they show under *Needs owner*; they cannot be enabled
      until reassigned; *Assign a new owner* works; *Delete* removes them; audit log entries exist for each rule (`/audit`). Same for a department.
- [x] Usage shows the rule on a request that a rule routed (request details, "Routing rule").
- [x] A `department-admin` and a `viewer` do not see the nav item and are sent to the forbidden page when opening `/routing-rules`.
- [x] Session expiry / 401 behaves as on other pages.

**Live gateway behaviour with the UI-created rules**
- [x] Send a synthetic request to `ume/chat-standard` with `x-ume-tier: premium`: response has `x-ume-rule` and a different `x-ume-provider`; without the header no `x-ume-rule`.
- [x] A change in the UI takes effect without restarting the gateway (catalogue invalidation over Redis; default cache TTL is 30 s as a fallback).
- [x] A rule scoped to a key keeps working after that key is rotated.
- [x] `budget_used` rule with a real budget; `tokens_used` rule with a key that has a tokens-per-minute limit (not covered by a live automated test because of minute-window flakiness).

## Task 2: screen reader pass (manual) and axe

**Status 2026-10-09: automated part done, manual part OPEN.** No screen reader has been run on this UI: an agent cannot listen to NVDA or VoiceOver, so nothing below is verified by ear. Automated evidence that exists: axe (WCAG 2.2 A/AA) on every page and on the drawers and dialogs of the routing rules page in all three themes, reflow at 320 px and 200 %, and `routingRulesFocus.spec.ts` (focus to the detail heading, back to the trigger, to the first invalid field and to the test result; heading levels do not skip; every control has a name; move buttons use `aria-disabled` and stay focusable; owner names carry `lang="sv"`; the `datalist` inputs are wired). Still to do by a person with a screen reader: how the datalist suggestions are announced, the live regions (no double or missing announcements), the segmented controls, the `details` disclosures, and the Swedish text. Record tool, browser and version in the Findings log when done.

Use at least NVDA + Firefox or Chrome on Windows; VoiceOver + Safari if available. Test in light, dark and lumen, at 200 % zoom and at 320 px width.
Record the exact screen reader, browser and version in the findings log. Nothing here is a certification: the accessibility statement says automated and manual evidence is still open for the *new* UI as a whole.

Things worth special attention on this feature (known risks, not known failures):

- **`<datalist>` model-name inputs** (rule form targets/fallbacks and the Test dialog): datalist support in screen readers is inconsistent. If suggestions are not announced or not reachable
  by keyboard, replace with an accessible combobox (Reka UI has one) or plain text plus a documented hint.
- **Live regions**: condition status (`role="status"` `aria-live="polite"` under the textarea; debounced 400 ms), the "now checked N of M" announcement after moving a rule (`role="status"` in `RoutingRulesView`),
  and `role="alert"` inline errors. Check for double or missing announcements, and that the textarea's `aria-describedby` reads sensibly.
- **Focus management**: selecting a rule moves focus to the detail heading; drawer/dialog open and close (focus trap, return to trigger); first invalid field gets focus on a failed submit;
  the test result heading gets focus after *Run test*; after deleting a rule or team where focus lands.
- **Landmarks and headings**: the rule list is `nav aria-label="Routing rules"` with `h2` section headings, the detail is an `h2` with `h3` sections; the page `h1` is "Routing rules". Check the outline.
- **SegmentedControl**: `mode="filter"` uses `aria-pressed` buttons (scope filter), `mode="radio"` uses a radiogroup with arrow keys (scope picker in the form and reassign dialog). Confirm both are understandable.
- **Icon + text status** in the dry run (check / close / help icons next to "true" / "false" / "could not be evaluated"): icons are `aria-hidden`; the words carry the meaning. Confirm nothing relies on colour.
- **`<details>` disclosures** ("Variables and examples", "Headers and request parameters", "Usage values").
- **Move earlier/later** are icon-only buttons with `aria-label`; they use `aria-disabled` and stay focusable at the ends. Confirm the disabled state is announced.
- **`lang="sv"`** on team/department/owner names and on server (Swedish) messages.
- Target size: **decided 2026-10-09 (user): the design target is WCAG 2.2 AA, 24 px minimum (SC 2.5.8).** The old e2e spec asserts 44 px (AAA 2.5.5), which is stale. Change the assertion to >= 24 px (do not resize components). The current 36 px (`h-ctl`) and 32 px (`sm`) controls already satisfy it; verify any smaller controls (icons, inline links excepted by the criterion) as part of the axe/e2e pass.


Also run the axe scan (see Task 3) on `/routing-rules` in all three themes with the drawer, the Test dialog, the reassign dialog and the scoped-rules dialog **open**, since the rule violations usually hide in overlays.

## Task 3: update the Playwright e2e tests to the current design

`tests/e2e` (`@playwright/test` 1.63, config in `playwright.config.ts`, helpers in `helpers.ts`, specs in `specs/`) was written for an earlier UI and does **not** match the current one. Verified differences:

| Old tests assume | Current UI |
|---|---|
| Swedish labels: "Logga in", "Granska ändringen", "Huvudmeny", "Hoppa till huvudinnehåll" | English: link **Sign in**, no review step, `nav` named **Main** (also **Quick access** on mobile), skip link **Skip to main content** |
| Routes like `/nycklar/ny`, `/nycklar/:id`, `/installningar` | `/keys`, `/organisation`, `/routes`, `/routing-rules`, `/settings`, ... (see `src/admin-ui/src/router/index.ts`) |
| Form ids like `#departments-form-name`, `#key-teamId`, `#settings-theme` | New ids, e.g. `#team-name`, `#rule-name`, `#rule-condition`, `#rule-target-0`; read the component source |
| Confirmation `alertdialog` with a checkbox ("review changes") | `ConfirmDialog` (a normal dialog with a confirm button), drawers for forms |
| Themes `light`, `dark`, `lumen`, `hc-dark`; `localStorage` key `ume-admin.theme`; locale switch | Themes `light`, `dark`, `lumen`; key `ume-theme`; English only |
| 44 px control assertion | Assert >= 24 px (AA 2.5.8), decided 2026-10-09 |
| `pages` list in `helpers.ts` | Build the list from `src/admin-ui/src/router/index.ts` |

What to do:

1. Make a plan before editing: run the three specs once against the stack, record how each fails, then rewrite `helpers.ts` (login, `ready`, `confirm`) first, then `journeys.spec.ts`, `demo.spec.ts` and `accessibility.spec.ts`.
2. Keep the intent of each existing test (OIDC login, show-once key and rotation, usage, budget, audit, fallback / PII on-prem / budget block demo, axe in every theme, reflow at 320 px / 200 %, keyboard skip link and focus).
3. **Add routing rules journeys**: create a rule through the UI, see live validation, test a request, move and disable it, send a real gateway request and check `x-ume-rule`, delete a team with rules choosing *Deactivate*,
   find the rule under *Needs owner*, reassign it, and confirm it applies again. Add `/routing-rules` and its overlays to the axe and reflow tests.
4. Existing constraints worth keeping: artifacts (trace/screenshot/video) are off on purpose so show-once keys cannot be captured; the helper paces admin requests to stay under the real 120 requests/minute limit;
   dev passwords come from user secrets or `E2E_PASSWORD`.
5. `README.md` and `docs/demo-script.md` claim these e2e suites as "repeatable automated demo evidence". Correct those statements to match what actually passes when you are done.

## Definition of done

- Every box in Task 1 ticked or turned into a finding.
- Screen reader pass recorded in the findings log (tool, version, what was tested) and `docs/accessibility-statement.md` (Swedish) updated to say exactly what has and has not been verified. No certification claims.
- `tests/e2e` passes against the running stack, includes the routing rules journeys, and `npm run typecheck` in `tests/e2e` is clean.
- All findings are fixed or explicitly parked with the user's agreement. The unit/component/API gates still pass (commands above).
- `docs/routing-rules-plan.md` step 8g caveats and this file's status updated; `CHANGELOG.md` has an entry.

## Findings log

Append new findings here (newest last). One row each; update the status when fixed or decided. Include how you found it and how to reproduce.

| Date | Area | Finding | Severity | Status |
|---|---|---|---|---|
| 2026-10-09 | tests/e2e | Playwright specs target an older UI (Swedish labels, old routes, `hc-dark` theme, review dialog). Not run against the current UI. | High (no working browser regression suite) | Open (Task 3) |
| 2026-10-09 | Deploy | New tables need `ume_admin` write grants (`deploy/postgres/app-roles.sql` updated). Not verified with the separate DB roles; `Test-Deployment.ps1` not run. | High until verified | Open (Task 1) |
| 2026-10-09 | A11y | No axe scan or screen reader pass on the new page. `datalist` model inputs are a known risk. | Medium | Open (Task 2) |
| 2026-10-09 | A11y / design | e2e asserts 44 px controls; current design uses 36 px (`h-ctl`) and 32 px (`sm`) controls. User decided 2026-10-09: target is AA 24 px; update the e2e assertion, keep components. | Medium | Decided (apply in Task 3) |
| 2026-10-09 | Docs | `docs/demo-script.md` and `HANDOVER.md` still say the e2e suites pass ("12 real-stack tests"). Stale. | Low | Open (Task 3, item 5) |
| 2026-10-09 | HANDOVER.md | Earlier known gaps noted there (route form lost its up/down reorder buttons; Ops page has no provider drain/resume action) were not part of this feature and were not re-checked. | Low | Unverified |
| 2026-10-09 | tests/e2e | Task 3 done. Rewrote `helpers.ts`, `journeys.spec.ts`, `demo.spec.ts`, `accessibility.spec.ts` and added `routingRules.spec.ts`. Run against the stack from `scripts/Start-E2E.ps1`: **13 of 17 passed** on the first run; after the fixes in the rows below, **all 17 pass** (journeys 5/5, demo 1/1, routing rules 2/2, axe on all pages and on drawers and dialogs in 3 themes, reflow, keyboard, forced colours). `npm run typecheck` is clean. | Done | Closed (Task 3) |
| 2026-10-09 | Dev stack | `aspire start` fails here: `migrations` exits 1 with `InvalidOperationException: Sequence contains no elements` in `DevSeeder.EnsureDevKeyAsync` (`FirstAsync(t => t.Name == "Digitalisering & AI")`). The local Postgres volume already has departments but not that team, so seeding is skipped and the dev key step throws. Fix the seeder to skip when the team is missing, and/or reset the volume. | Medium | Closed. Fixed: `DevSeeder.EnsureDevKeyAsync` returns when the demo team is missing instead of throwing. |
| 2026-10-09 | A11y | The "Skip to main content" link is `md:hidden`, so there is no skip link from 768 px up. The sidebar has about 15 tab stops before `main`. Landmarks (`nav`, `main`) are a sufficient technique, so this is not a failure, but a skip link is cheap. The e2e test now asserts the skip link only on small screens. | Low | Open (decide) |
| 2026-10-09 | UI copy | `SettingsView` says "Theme and display options are in the top bar", but the theme control is in the sidebar and only visible from 1024 px. Below that, there is no way to change theme. | Low | Open |
| 2026-10-09 | A11y (axe `definition-list`) | `DetailSection` always renders a `<dl>`, but the key drawer's *Secret key* and *Budgets* sections put a `code`, buttons, paragraphs and a component inside it. Fails in all three themes. Fix: let `DetailSection` render a `div` when its content is not `DetailRow`s. Found by `drawers and dialogs pass axe`. | Medium | Closed. Fixed: `DetailSection` has a `list` prop; *Secret key* and *Budgets* use `:list="false"` (a `div`). |
| 2026-10-09 | A11y (axe `list`) | `RuleFormDrawer` target list: `<li role="group" aria-label="Target n">` replaces the listitem role, so the `ul` has no list items. Fails in all three themes. Fix: put the `role="group"` and label on a `div` inside the `li`. | Medium | Closed. Fixed: `role="group"` moved to a `div` inside the `li`. |
| 2026-10-09 | A11y (axe `color-contrast`) | Dark theme: white text on `bg-danger` (the `danger-solid` button, e.g. *Delete rule*, *Delete team*) is below 4.5:1. Light and lumen pass. Fix the dark `--danger` token or the button text colour. | Medium | Closed. Fixed: new `--danger-fg` token (dark `#2A0A0D`, others white), used by `danger-solid` buttons and the nav badge. |
| 2026-10-09 | Reflow | Overview at 320 px scrolls horizontally by 16 px: the *Daily spend* and *Providers* cards are 320 px wide inside a 288 px column (probably a grid track with a minimum width). All other pages reflow. Fix, then rerun `all pages reflow`. | Medium | Closed. Fixed: Overview grid uses `minmax(min(320px,100%),1fr)`. |
| 2026-10-09 | A11y (AAA, informational) | The suite no longer checks WCAG AAA. axe `color-contrast-enhanced` (7:1) fails in light on most text; the old suite met AAA only through the removed high-contrast themes. Target is AA by decision. | Info | Parked (AA is the target) |
| 2026-10-09 | A11y (2.5.8) | Target size is checked with the AA rule including the spacing exception (24 px circle must not touch another target). Everything passes. Sortable table header buttons are 16 px tall but isolated, so they pass only through the exception. | Info | Closed |
| 2026-10-09 | Tests / dev env | The Aspire user secrets of this machine set `Seed:Enabled=False` and `FakeLlm:Enabled=False`. `Start-E2E.ps1` overrides both through environment variables, which win over user secrets. Without the fake LLM every gateway request returns 503. | Info | Closed (handled in the script) |
| 2026-10-09 | Docs | `HANDOVER.md` no longer contains the stale "12 passing" and "Tailwind 3" claims the earlier row mentioned. `README.md` and `docs/demo-script.md` were corrected to the real numbers. | Low | Closed |
| 2026-10-09 | Deploy | `deploy/Test-Deployment.ps1` passes (build, hardened Compose, TLS/ACL/roles, SPA, backup and restore). The role grants for `"RoutingRules"` and `"RoutingRuleTargets"` work: `deploy/tests/verify-data.sh` now inserts, updates and deletes a rule with its target as `ume_admin` (rolled back), reads them as `ume_gateway` and checks that `ume_gateway` cannot write them. | Closed | Closed (Task 1) |
| 2026-10-09 | Deploy tests | `deploy/tests/spa-smoke.mjs` still looked for the Swedish "Logga in" link, so the deployment test failed after the UI became English. Fixed to "Sign in". | Medium | Closed |
| 2026-10-09 | Session expiry | With an expired session every `/api/*` call got a 302 to Keycloak instead of a 401 (OIDC is the default challenge). `fetch` followed it, failed on CORS, and the UI's 401 handler never ran, so the user saw a dead page instead of the sign-in page. Same on all pages. Fixed in `AdminApi/Program.cs` (`OnRedirectToIdentityProvider` answers 401 for `/api`); `/bff/login` still redirects. Covered by e2e. | High | Closed |
| 2026-10-09 | Audit | The audit page's *Entity type* filter had no `RoutingRule`, so rule changes could not be filtered. Added. | Low | Closed |
| 2026-10-09 | UX (note) | A duplicate rule name (409) is shown as a message at the top of the form, not on the *Name* field. Server errors for an unknown model, a scope without owner and a bad condition do land on their fields. | Low | Open (decide) |
| 2026-10-09 | Backend tests | Flaky tests, two separate causes. (1) AdminApi: all test classes share one Postgres and ran in parallel, but some tests act on the whole database (config export/import re-creates or collides with entities other tests create and delete, global rules, the exchange rate), giving a 409 in a different test each run. Fixed: `[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]` in `AdminFixture.cs` so the assembly runs serially (about 8 s either way). (2) Gateway `Models_list_filters_by_provider_allowlist` asserted that every id in the key's model list starts with `onprem/`, but other tests create `test/<guid>` route aliases that point at that provider and they show up too (a test-order dependency, it also failed when run serially after them). Fixed: it now checks that `onprem/ok` is listed and that no model of another provider is. Verified: AdminApi 6 of 6, Gateway 10 of 10, whole solution 3 of 3 (380 tests). Rule for new tests: do not assert on the whole content of a shared table or list; assert on your own rows. | Medium | Closed |
| 2026-10-09 | Task 1 | Everything else on the Task 1 checklist behaved as specified and is now covered by `routingRules*.spec.ts`: list, filter counts, helpers, live validation including an unreachable checker, weights and shares, field-level server errors, edit, change scope, enable/disable, reorder changes gateway behaviour, test dialog (could not evaluate, chain steps, missing models), team and department deletion with *Deactivate* and *Delete*, rule shown in Usage, `viewer` and `department-admin` get the forbidden page, a key scoped rule survives rotation, `budget_used` and `tokens_used` read the key's own values, changes apply without restarting the gateway. | Info | Closed |
