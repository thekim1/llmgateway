# Changelog

## Unreleased

Browser tests (`tests/e2e`) rewritten for the current admin UI (English labels, drawers, `ume-theme`, three themes,
WCAG 2.2 AA with the 24 px target-size rule) and extended with routing rules journeys. New `scripts/Start-E2E.ps1` runs the stack
on a separate, disposable test database. All 34 tests pass (routing rules journeys, UI details, permissions, key rotation, usage signals, audit, focus). The run found four accessibility defects, now fixed: key drawer `dl` markup, rule drawer target list,
dark-theme contrast of destructive buttons (`--danger-fg`), and Overview overflow at 320 px. The dev seeder no longer
crashes on a database without the demo team. An expired session now gives the UI a 401 on `/api` calls instead of a redirect to the identity provider, the audit filter lists `RoutingRule`, the deployment test checks the routing rule role grants, and its SPA smoke test follows the English UI.

Routing rules (in progress, see `docs/routing-rules.md` and `docs/routing-rules-plan.md`):

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
  runbook entry for disabling a misbehaving rule, and `docs/handover-ui-verification.md` for the remaining verification (full stack, screen reader, e2e rewrite).
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
