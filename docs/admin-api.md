# Admin API contract (control plane)

Base path: same origin as the admin UI (BFF pattern). JSON uses **camelCase**, enums are serialised as
**strings** (e.g. `"RevokeImmediately"`), timestamps are ISO-8601 with offset, money is SEK unless the field
name says `Usd`. Errors use RFC 9457 `application/problem+json`:
`{ "type", "title", "status", "detail", "errors": { "field": ["message"] } }` (Swedish messages).

## Authentication (BFF)
- `GET /bff/login?returnUrl=/path` → 302 to OIDC provider (Keycloak in dev, Entra ID/AD FS in prod).
- `POST /bff/logout` → signs out (cookie + OIDC end-session).
- Logout returns `{ redirectUrl }`; navigate to this URL to complete OIDC provider signout.
  The end-session request includes `client_id` and protected state for the local return to `/`.
  Register `/signout-callback-oidc` as an allowed post-logout redirect at the identity provider.
  Tokens are not saved; providers such as Keycloak may therefore require logout confirmation.
- `POST /bff/session/extend` → `{ sessionExpiresAt }`, renews an authenticated session for eight
  hours without losing unsaved forms. Requires the same CSRF headers as other mutations.
- `GET /bff/user` → `{ isAuthenticated, name, email, roles: string[], departmentCodes: string[], sessionExpiresAt }`
  (always 200; `isAuthenticated=false` when not signed in).
- Session cookie: `__Host-ume-admin`, HttpOnly, Secure, SameSite=Lax (compatible with OIDC redirects).
  No tokens reach the browser. Sessions last eight hours; the UI warns before expiry and can start a new login.
- **CSRF**: every non-GET request must send header `X-XSRF-TOKEN` with the value of the readable cookie
  `XSRF-TOKEN` (set on `GET /bff/user`). Also send `X-Requested-With: XMLHttpRequest`.
- Unauthenticated API calls → 401 (no redirect). Missing role → 403.
- Local configuration: `Oidc:Authority`, `Oidc:ClientId` (default `ume-admin`), optional
  `Oidc:ClientSecret`, `ConnectionStrings:gatewaydb`, `Security:KeyPepper`, optional Redis and
  Data Protection certificate settings shared with the gateway. Production OIDC metadata requires HTTPS.
- API rate limit: 120 requests/minute per authenticated subject (`Admin:RequestsPerMinute`, 1–10000).
  Exceeded → 429 ProblemDetails with `Retry-After`.

Roles (OIDC `roles` claim):
| Role | Can |
|---|---|
| `gateway-admin` | everything |
| `department-admin` | manage teams, keys and budgets in own förvaltning(ar) (`departmentCodes` claim = ansvarskod), read usage for them |
| `viewer` | read-only: catalogue, own department usage |

## Enums
- `DataResidency`: `OnPrem`, `Eu`, `External`
- `ProviderType`: `OpenAI`, `AzureOpenAI`, `AzureAIFoundry`, `Anthropic`, `Ollama`, `OllamaCloud`, `OpenAICompatible`
- `ProviderAuthMode`: `None`, `Bearer`, `ApiKeyHeader`, `XApiKeyHeader`
- `ProviderCapabilities` (array of): `ChatCompletions`, `Embeddings`, `Responses`, `AnthropicMessages`, `Streaming`
- `ModelKind`: `Chat`, `Embedding`; `ParameterProfile`: `Standard`, `OpenAIReasoning`
- `PiiPolicy`: `Off`, `Allow`, `Redact`, `Block`, `RerouteToOnPrem`
- `BudgetScope`: `Department`, `Team`, `VirtualKey`; `BudgetPeriod`: `Daily`, `Weekly`, `Monthly`, `Quarterly`, `Yearly`
- `KeyRotationMode`: `RevokeImmediately` (default), `Grace24Hours`
- `KeyStatus`: `Active`, `InGracePeriod`, `Expired`, `Revoked`, `Disabled`
- `RequestOutcome`: `Success`, `ProviderError`, `BudgetExceeded`, `RateLimited`, `PiiBlocked`, `Rejected`, `ClientCancelled`
- `CircuitState`: `Closed`, `Open`

## Organisation
- `GET /api/departments` → `Department[]` where `Department = { id, name, costCenterCode, isActive, teamCount, createdAt }`
- `POST /api/departments` `{ name, costCenterCode }` → 201 `Department` *(gateway-admin)*
- `PUT /api/departments/{id}` `{ name, costCenterCode, isActive }` → `Department`
- `DELETE /api/departments/{id}` → 204 (409 if it has teams)
- `GET /api/teams?departmentId=` → `Team[]` where `Team = { id, departmentId, departmentName, name, description, isActive, keyCount, createdAt }`
- `POST /api/teams` `{ departmentId, name, description? }` → 201 `Team`
- `PUT /api/teams/{id}` `{ name, description?, isActive }` → `Team`
- `DELETE /api/teams/{id}` → 204 (409 if it has keys)

## Virtual keys
`VirtualKey = { id, teamId, teamName, departmentId, departmentName, name, description, prefix, status: KeyStatus,
isEnabled, createdAt, createdBy, expiresAt, revokedAt, graceUntil, lastUsedAt, allowedModels: string[],
allowedResidencies: DataResidency[], allowedProviders: string[] (provider names, empty = all; omitted on update = unchanged), canReveal, piiPolicy, requestsPerMinute, tokensPerMinute, rotatedToKeyId }`
(empty `allowedModels` / `allowedResidencies` = all allowed)

- `GET /api/keys?teamId=&departmentId=&status=` → `VirtualKey[]`
- `GET /api/keys/{id}` → `VirtualKey`
- `POST /api/keys` `{ teamId, name, description?, expiresAt?, allowedModels, allowedResidencies, piiPolicy, requestsPerMinute?, tokensPerMinute? }`
  → 201 `{ key: VirtualKey, secret: "ume-sk-…" }` — **secret is shown once, never retrievable again**
- `PUT /api/keys/{id}` `{ name, description?, expiresAt?, allowedModels, allowedResidencies, piiPolicy, requestsPerMinute?, tokensPerMinute?, isEnabled }` → `VirtualKey`
- `POST /api/keys/{id}/rotate` `{ mode: KeyRotationMode }` → `{ key: VirtualKey, secret, previousKey: VirtualKey }`
- `POST /api/keys/{id}/reveal` `{ purpose: "Reveal" | "Copy" }` → `{ secret }` (gateway-admin only; writes an audit entry `reveal`/`copy` with the actor; 409 for revoked keys and for keys created before the secret was stored, `VirtualKey.canReveal` is false for those)
- `POST /api/keys/{id}/revoke` → `VirtualKey` (immediate, also cancels grace)
- Rotation retains the predecessor's key budgets and spend across the full rotation lineage, including
  the grace-period key. It does not reset the remaining budget. Concurrent conflicting mutations → 409.

## Providers, models, routes
- Plain HTTP is accepted only for on-prem local/test configuration. Production gateway
  invocation requires HTTPS even for on-prem targets and never follows upstream redirects.
  Configure approved TLS endpoints before enabling production providers.
- `Provider = { id, name, displayName, type, baseUrl, authMode, hasCredential, residency, capabilities: ProviderCapabilities[], isEnabled, isDrained, timeoutSeconds, createdAt, deploymentCount }`
  (credential is **write-only**, never returned)
- `GET /api/providers` · `POST /api/providers` `{ name, displayName?, type, baseUrl, authMode, credential?, residency, capabilities, timeoutSeconds, isEnabled }`
- `PUT /api/providers/{id}` same body; `credential`: omitted/null = unchanged, `""` = remove
- `POST /api/providers/{id}/drain` `{ drained: boolean }` → `Provider` · `DELETE /api/providers/{id}` (409 if models exist)
- `POST /api/providers/{id}/discover-models` → `DiscoveredModel[] = { id, displayName, kind, parameterProfile, contextWindow|null, features: string[], price: { inputPerMillionUsd, cachedInputPerMillionUsd, outputPerMillionUsd }|null, alreadyAdded }`. Calls the provider's `/models` with the stored credential, so it doubles as a connection test (502/504 on failure). Context size, features and price are included only when the provider reports them. Azure OpenAI is unsupported (400).
- `Price = { inputPerMillionUsd, cachedInputPerMillionUsd, outputPerMillionUsd, effectiveFrom }`
- `Model = { id, providerId, providerName, residency, name, upstreamModel, kind, parameterProfile, contextWindow, isEnabled, features: string[], currentPrice: Price|null }`
- `GET /api/models` · `POST /api/models` `{ providerId, name, upstreamModel, kind, parameterProfile, contextWindow?, isEnabled, features?: string[], price?: Price }`
- `PUT /api/models/{id}` `{ name, upstreamModel, kind, parameterProfile, contextWindow?, isEnabled, features?: string[] }` · `DELETE /api/models/{id}` (409 if used by a route)
- `GET /api/models/{id}/prices` → `Price[]` · `POST /api/models/{id}/prices` `Price` (effectiveFrom optional = now)
- `Route = { id, name, description, kind, isEnabled, targets: [{ modelId, modelName, providerName, residency, priority, weight }] }`
- `GET /api/routes` · `POST /api/routes` · `PUT /api/routes/{id}` body `{ name, description?, kind, isEnabled, targets: [{ modelId, priority, weight }] }` · `DELETE /api/routes/{id}`

## Routing rules
Concepts, condition language and evaluation order: [routing rules](routing-rules.md). All endpoints require
`gateway-admin`. Mutations are audited (`RoutingRule`: `create`, `update`, `delete`, `reassign`, `reorder`, `deactivate`)
and invalidate the gateway catalogue.
- `RoutingRule = { id, name, description, isEnabled, priority, scope: "Global"|"Department"|"Team"|"VirtualKey", scopeId|null,
  scopeName|null, isOrphaned, condition, chain, targets: [{ model, weight }], fallbacks: string[],
  validationErrors: [{ message, position|null, length|null }], createdAt, updatedAt }`
  - `isOrphaned`: the key/team/department the rule is scoped to no longer exists. Such a rule never applies and cannot be enabled until reassigned.
  - `validationErrors`: why the gateway ignores an enabled rule (empty when valid).
- `GET /api/routing-rules?scope=&scopeId=&orphaned=` (ordered by scope, then priority) · `GET /api/routing-rules/{id}`
- `POST /api/routing-rules` · `PUT /api/routing-rules/{id}` body
  `{ name, description?, scope, scopeId?, isEnabled, priority, condition?, chain, targets: [{ model, weight (1..1000000) }] (1..20), fallbacks?: string[] }`
  → `RoutingRule`. `model` is a route alias or model name. Validation → 400 with field errors (`condition` messages include the character position,
  `targets`, `fallbacks`, `scopeId`); a non-chained rule may only target existing names, fallbacks must always exist; name unique per scope (409, case-insensitive);
  the scope owner must exist (an already disabled orphaned rule may still be edited while it stays disabled).
- `DELETE /api/routing-rules/{id}` → 204
- `POST /api/routing-rules/{id}/reassign` `{ scope, scopeId?, enable = true }` → `RoutingRule`. Attaches the rule to a new team, department or key (or makes it global) and, by default, enables it.
- `POST /api/routing-rules/reorder` `{ scope, scopeId?, ruleIds: Guid[] }` → `RoutingRule[]`. Sets priorities 0, 10, 20, … in the given order; every id must belong to that scope.
- `POST /api/routing-rules/validate` `{ condition }` → `{ valid, errors: [{ position, length, message }], variables: string[], available: [{ name, type }] }` (live validation while typing).
- `POST /api/routing-rules/test` (dry run) `{ model, endpoint?, headers?, params?, keyId?, teamId?, departmentId?, budgetUsed?, tokensUsed?, piiDetected?, promptTokens?, seed? }`
  → `{ matched, primaryModel, models: [{ name, exists, type, kind, enabled }], applied: [{ ruleId, name, fromModel, toModel }], chainLimitReached,
  evaluation: [{ ruleId, name, scope, priority, chainStep, model, outcome: "Matched"|"NotMatched"|"Skipped", trace: [{ text, leftValue, result|null }] }],
  ignoredRules, note }`. Evaluates the **stored, enabled** rules. `keyId` fills in team, department and key name (and the key's rotation lineage);
  usage values that are not supplied are unknown, so conditions using them do not match. `seed` makes weighted choices repeatable.
- **Deleting a team or department that has rules.** `DELETE /api/teams/{id}` and `DELETE /api/departments/{id}` take an optional query parameter
  `routingRules=delete|deactivate`. If rules are scoped to the team/department and the parameter is missing, the call fails with **409** and
  `{ code: "routing_rules_scoped", choices: ["delete","deactivate"], rules: [{ id, name, isEnabled }], detail }` so the client can ask the user.
  `delete` removes the rules; `deactivate` disables them and keeps everything else, so they can be attached to a new team/department with
  `reassign`. The choice and the delete are saved in one transaction and audited per rule. Any other value → 400.
- **Names in use.** A route alias or model that a rule uses as target or fallback cannot be deleted or renamed (409 naming the rule). Conditions that compare `model` with a name are not checked.
- Key rotation: a rule scoped to a key keeps applying to the key that replaced it, like budgets.

## Budgets & alerts
- `Budget = { id, scope, scopeId, scopeName, limitSek, period, alertThresholds: number[], isActive, periodStart, periodEnd, spentSek, percentUsed }`
- `GET /api/budgets?scope=&scopeId=` · `POST /api/budgets` `{ scope, scopeId, limitSek, period, alertThresholds, isActive }` · `PUT /api/budgets/{id}` · `DELETE /api/budgets/{id}`
- `GET /api/alerts?acknowledged=false` → `[{ id, budgetId, scope, scopeName, thresholdPercent, spentSek, limitSek, periodStart, timestamp, acknowledged }]`
- `POST /api/alerts/{id}/acknowledge` → 204
- `GET /api/settings/exchange-rate` → `{ currency: "USD", sekPerUnit, effectiveFrom }` · `PUT /api/settings/exchange-rate` `{ sekPerUnit }`

## Usage (metadata only – never prompt/response content)
- `GET /api/usage/summary?from=&to=&groupBy=department|team|key|model|provider|day&departmentId=&teamId=`
  → `{ from, to, totalCostSek, totalRequests, totalInputTokens, totalOutputTokens, totalErrors, rows: [{ key, label, requests, inputTokens, outputTokens, costSek, errors, fallbacks }] }`
- `GET /api/usage/requests?from=&to=&keyId=&teamId=&departmentId=&outcome=&page=1&pageSize=50`
  → `{ items: UsageRequest[], total }` where `UsageRequest = { requestId, timestamp, keyPrefix, keyName, teamName, departmentName, endpoint, requestedModel, providerName, upstreamModel, inputTokens, cachedInputTokens, outputTokens, costSek, latencyMs, statusCode, outcome, fallbackCount, streamed, piiActionApplied, piiCategories, errorCode, routingRuleId, routingRuleName }` (`routingRuleName`: the routing rule that decided where the request went, `null` when none applied)
- `GET /api/usage/requests/{requestId}` → `UsageRequest` (lookup by `x-request-id` from gateway response)
- `GET /api/usage/export.csv?from=&to=` → CSV: ansvarskod, förvaltning, team, nyckel, requests, tokens, kostnad SEK

## Catalogue (developer portal, any authenticated user)
- `GET /api/catalog` → `{ gatewayBaseUrl, routes: [{ name, description, kind, residencies: DataResidency[], capabilities: ProviderCapabilities[], inputSekPerMillion, outputSekPerMillion }], models: [{ name, kind, residency, capabilities, inputSekPerMillion, outputSekPerMillion }] }`

## Operations (gateway-admin)
- `GET /api/ops/health` → `{ checkedAt, components: [{ name, status: "Healthy"|"Degraded"|"Unhealthy", description }], versions: { adminApi, gateway: string|null, schema }, usageWriter: { queueDepth, capacity, inFlightRecords, lastWriteAt: string|null, consecutiveFailures }|null, providers: [{ id, name, type, residency, isEnabled, isDrained, circuitState, requests24h, errorRate24h, fallbackRate24h, p50LatencyMs, p95LatencyMs }] }`
- Gateway status/version/queue are fetched live over `Gateway:OperationsUrl` (HTTPS; defaults to
  `Gateway:BaseUrl`) within five seconds. An unavailable, malformed or unauthenticated probe
  yields an explicit `Unhealthy` gateway component and null version/usageWriter, never fabricated
  values. Probe failures log only a safe reason. `lastWriteAt=null` means no successful writes yet.
- The gateway's metadata-only `GET /health/operations` requires a 30-second timestamp-bound,
  purpose-separated HMAC signature using the shared pepper; it has no admin mutation capability.
  Unsigned/stale/invalid requests are 401. The pepper/signature are never returned to the browser.
  Usage write failures retain the in-flight batch for retry and report Degraded until recovery;
  queue persistence across process/host loss is not guaranteed.
- `POST /api/ops/providers/{id}/circuit` `{ state: "Open" | "Closed" }` → 204 (force open = take out of rotation for 15 min)
- `POST /api/ops/cache/invalidate-keys` → 204
- `GET /api/ops/config/export` → JSON document (providers without credentials, models, prices, routes, global routing rules)
- `POST /api/ops/config/import` (same JSON) → `{ created, updated, skipped }`
- Config schema: `{ schemaVersion: 1, providers: [{ configuration: ProviderRequest (without credential),
  credentialEnvironment?: "UME_PROVIDER_..." }], models: [{ providerName, configuration: ModelRequest,
  prices: Price[] }], routes: [{ name, description, kind, isEnabled,
  targets: [{ modelName, priority, weight }] }], routingRules?: [{ name, description?, isEnabled, priority, condition?, chain,
  targets: [{ model, weight }], fallbacks?: string[] }] }`. Only **global** routing rules are transferred (key/team/department ids differ between
  environments); rules are matched by name, created or updated, never deleted by import, and each is validated like one saved through the API (an invalid rule fails the whole import).
  Documents without `routingRules` (older exports) still import.
  References use stable names; database IDs in model configuration are ignored on import.
  Import is transactional, rejects plaintext credentials, preserves existing encrypted credentials when
  a reference is omitted, and never overwrites price history. Environment references must already exist
  in the Admin API process. Export never contains secret values or encrypted credentials.
- `GET /api/audit?page=&pageSize=&entityType=` → `{ items: [{ id, timestamp, actor, action, entityType, entityId, details }], total }`

## Gateway (data plane) – for the developer portal snippets
- Base URL: `{gatewayBaseUrl}/v1`. Auth: `Authorization: Bearer ume-sk-…` (also `x-api-key` / `api-key`).
- `POST /v1/chat/completions`, `POST /v1/embeddings`, `GET /v1/models`, `POST /v1/responses`, `POST /v1/messages` (Anthropic format).
- Response headers: `x-request-id`, `x-ume-provider`, `x-ume-model`, `x-ume-fallbacks`, `x-ume-cost-sek`,
  `x-ratelimit-limit-requests`, `x-ratelimit-remaining-requests`, `x-ume-budget-remaining-sek`,
  `x-ume-rule` (id(s) of the routing rule(s) that applied; see [routing rules](routing-rules.md)).
- Error body (OpenAI style): `{ "error": { "message", "type", "code", "request_id", "doc_url" } }` with codes
  `invalid_api_key`, `key_expired`, `key_revoked`, `key_disabled`, `model_not_allowed`, `model_not_found`, `budget_exceeded`,
  `rate_limited`, `pii_blocked`, `no_eligible_provider`, `all_providers_failed`, `invalid_request`.

**Key budgets.** A key can have one budget per period (`Hourly`, `Daily`, `Weekly`, `Monthly`, `Quarterly`, `Yearly`) via `POST /api/budgets` with `scope: "VirtualKey"`; a second budget for the same owner and period returns 409. The gateway enforces every applicable budget (key, team, department) and rejects with 402 when any is exhausted. Hourly windows are aligned to the clock hour. `requestsPerMinute` and `tokensPerMinute` on the key are enforced per minute (429 `rate_limited`; token usage is counted after each response).
