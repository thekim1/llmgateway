# Routing rules: implementation plan

Modelled on Bifrost routing rules (https://docs.getbifrost.ai/providers/routing-rules). Rules sit in front of the existing alias resolution. `RouteAlias`, `RouteTarget`, the failover loop and the circuit breaker are unchanged.

## Decisions

- Condition language: CEL-syntax subset, parsed and evaluated in-house in `Ume.LlmGateway.Domain`. No new dependency, so no licence cost. It also gives validation errors with positions and a per-rule evaluation trace for the admin UI.
- Chaining is included, with a depth cap and convergence detection.
- Hard constraints are applied after rules (a key's model allow-list applies to the model the client requests, since keys list aliases and rules legitimately redirect to deployments): `AllowedModels`, `AllowedProviders`, residency, PII on-prem restriction, endpoint kind. A rule can never widen access.
- Delivered in steps. Every step ends with passing tests and the checklist below ticked.

## Open points for later steps

- Step 6: rules are evaluated on the requested name first (it may be an unknown legacy name that a rule rewrites), the allow-list is checked against the requested name, then the final models are resolved. The `endpoint` variable uses the values `chat_completions`, `embeddings`, `responses`, `anthropic_messages` (documented in docs/routing-rules.md).
- Step 7 (done): deleting a team or department asks whether to delete the rules or deactivate them until a new owner is assigned (there is deliberately no foreign key; an orphaned rule never matches). Keys are never deleted, only revoked or rotated.

## Rule model

name, description, enabled, priority (lower first), scope (VirtualKey | Team | Department | Global) + scope_id, condition (CEL string; empty = always), chain (bool), action:
- targets: `[{alias | deployment, weight}]`
- fallbacks: ordered list of alias/deployment

Evaluation: VirtualKey, then Team, then Department, then Global. Within a scope, ascending priority, and the first match wins. With chain=true the result becomes the new model and rules re-evaluate, stopping on no match, terminal match, no change, or depth cap.

## CEL variables

model, endpoint, headers["x"] (case-insensitive; missing = false), params["x"], key_id, key_name, team_id, team_name, department_id, department_name, budget_used (0-100, the budget closest to its limit), tokens_used (0-100, key TPM limit), pii_detected, prompt_tokens.
Operators: == != < <= > >= && || ! in, startsWith endsWith contains matches. Invalid expressions are rejected on save and skipped with a warning at runtime.

## Steps and checklist

1. Resolver extraction
   - [x] `IRouteResolver` extracted from `GatewayRequestHandler` (L98-133, L187-195), behaviour identical, existing tests green
2. Expression engine (Domain, pure)
   - [x] lexer, parser, evaluator, validation errors with positions
   - [x] unit tests for every operator, missing header, type errors, regex limits (`matches` with timeout)
3. Rule evaluation (Domain, pure)
   - [x] scope ordering, priority, weighted targets, fallbacks, chaining, convergence, depth cap
   - [x] tests incl. a rule that tries to escape the key's provider/residency limits
4. Persistence (docs: README, docs/routing-rules.md, CHANGELOG updated)
   - [x] `RoutingRule` entity, EF mapping, migration, `DevSeeder` sample rules
   - [x] loaded in `GatewayCatalog.LoadAsync`, compiled once per `CatalogSnapshot`, invalidated via `InvalidationKind.Config`
5. Budget peek
   - [x] read-only `BudgetService.PeekUsedPercentAsync` (no reservation) feeding `budget_used`; `IRateLimiter.PeekTokensUsedPercentAsync` feeding `tokens_used`; `RoutingRuleSet.References` so lookups only run when a rule needs them (docs updated)
6. Gateway wiring (docs: routing-rules.md, admin-api.md, architecture.md, upgrade-guide.md, README, CHANGELOG updated)
   - [x] rules run after the PII policy and before candidate selection (the requested name is checked first; a name only a rule knows is checked via the first model in its chain); reservation estimate uses the rule's candidate set
   - [x] `deploy/postgres/app-roles.sql` grants the admin role write access to the rule tables
   - [x] `x-ume-rule` response header, matched rule id on `UsageRecord` (migration), metrics labels bounded
   - [x] gateway integration tests (`GatewayFixture`): header tier, budget failover, A/B split, chain, no-match fallthrough
7. Admin API (docs: admin-api.md, routing-rules.md, runbook.md, README, CHANGELOG updated)
   - [x] CRUD `/api/routing-rules` with audit via `ctx.SaveAsync`, validation, reorder, `POST .../test` (dry-run a sample request, return trace)
   - [x] `ConfigTransfer` export/import (global rules), `docs/admin-api.md`
   - [x] deleting a team/department with rules asks the user: `routingRules=delete|deactivate` (409 + list otherwise); deactivated rules are orphaned until reassigned (`/reassign`)
   - [x] routes/models used by rules cannot be removed or renamed; key-scoped rules follow key rotation
8. Admin UI (verified against the current frontend on 2026-10-09)

   The admin UI is `src/admin-ui`: **Vue 3 + Pinia + Vue Router 5, Reka UI (headless primitives) and Tailwind CSS v4**, built with Vite,
   served through the AdminApi BFF. Styling uses only the token utilities from `tailwind.config.cjs` / `src/styles/tokens.css`
   (three themes: light, dark, lumen; never hard-coded colours). Shared components live in `src/components/ui` (`UiButton`, `UiDrawer`,
   `UiDialog`, `ConfirmDialog`, `TextField`, `SelectField`, `CheckField`, `SegmentedControl`, `FieldGroup`, `DataTable`, `UiBadge`, `UiCard`,
   `AsyncState`, `InlineError`, `PageHeader`, `AppIcon` = Material Symbols). Pages follow the *Routes* / *Organisation* pattern: `PageHeader` with the
   primary action, a master list on the left and a detail card on the right, forms in a `UiDrawer`, destructive actions in a `ConfirmDialog`, success as a
   toast, errors inline, thin Pinia stores on `useLoadable`, typed API in `src/api`, UI text in English with Swedish server messages marked `lang="sv"`.
   Checks: `npm run typecheck`, `npm run lint` (incl. `eslint-plugin-vuejs-accessibility`), `npm test` (Vitest + Vue Test Utils, API mocked).
   Note: `tests/e2e` (Playwright) still targets an older UI (Swedish labels such as "Granska ändringen", routes such as `/nycklar/ny`) and is **not**
   updated by this feature; it needs its own pass.

   - [x] 8a. Plumbing: types (`RoutingRule`, requests, dry-run result, scoped-rules problem), `api.routingRules.*`, `api.teams.remove` / `api.departments.remove` with the optional `routingRules` choice, `stores/routingRules.ts`, labels, nav item "Routing rules" (group *Routing*, `gateway-admin`), route `/routing-rules`, command palette entry (derived from nav)
   - [x] 8b. `RoutingRulesView`: master list grouped by scope in evaluation order (key, team, department, global) with order numbers, status badges (Disabled, Chain, Needs owner), scope filter with counts, banner when rules need an owner; detail card with condition, weighted targets (share %), fallbacks, validation problems the gateway would ignore; enable/disable, move up/down (keyboard-accessible, no drag), edit, reassign, delete
   - [x] 8c. `RuleFormDrawer` + `ConditionEditor`: scope + owner picker (as in Budgets), targets/fallbacks with suggestions from routes and models (chained rules may use other names), live validation against `/validate` (debounced, `aria-live`, error position highlighted), variable and example helpers, server field errors mapped to fields
   - [x] 8d. `RuleTestDialog` (dry run): sample request (model, key or team, headers, parameters, usage values) -> which rule applies, resulting models with "does not exist" warnings, per-rule evaluation with true/false/could-not-evaluate shown as text plus icon (not colour only)
   - [x] 8e. Delete-or-deactivate flow in *Organisation*: a 409 `routing_rules_scoped` opens a dialog that lists the rules and asks "Deactivate until a new team/department is assigned" (default) or "Delete the rules"; the choice is sent as `routingRules`; `ReassignRuleDialog` attaches orphaned rules to a new owner
   - [x] 8f. Visibility: show the rule that routed a request in *Usage* (`routingRuleName` added to the usage API and the request detail)
   - [x] 8g. 92 Vitest specs for the new components, stores, utilities and the Organisation delete flow (UI suite 121 tests); typecheck, `eslint --max-warnings 0` and production build clean; look and feel checked in screenshots of the built UI in light, dark and lumen and at phone width (against a stand-in API, **not yet on the full Aspire stack**); README, routing-rules.md, demo script, accessibility statement, CHANGELOG updated. Not done: axe scan and manual screen-reader pass for the new page, and the stale `tests/e2e` journeys
9. Docs (README, getting started and CHANGELOG are updated in every step; this step is the final pass). **Feature complete; verification open: [handover-ui-verification.md](handover-ui-verification.md)**
   - [x] `docs/architecture.md` (flow and a Routing rules section), ADR 013-014, `docs/runbook.md`, `CHANGELOG.md`, `HANDOVER.md`
   - [x] `docs/handover-ui-verification.md` written for the remaining work: full-stack check, screen reader pass, e2e rewrite (**open**, see its findings log)
