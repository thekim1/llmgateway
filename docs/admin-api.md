# Admin API contract (control plane)

Base path: same origin as the admin UI (BFF pattern). JSON uses **camelCase**, enums are serialised as
**strings** (e.g. `"RevokeImmediately"`), timestamps are ISO-8601 with offset, money is in the currency the field
name says (`Usd` or `Sek`; budgets and alerts are in USD, SEK amounts are for display). Errors use RFC 9457 `application/problem+json`:
`{ "type", "title", "status", "detail", "errors": { "field": ["message"] } }` (English messages).
A malformed request (unreadable body) → 400; an unexpected failure → 500 with the detail "An internal error occurred."
(the exception is only logged, by type). Every response, error responses included, carries `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY` and `Referrer-Policy: no-referrer`.
A change is saved before the gateways are told to reload. If they cannot be told (Redis unavailable), the request still
succeeds, the admin API logs a warning, and the response carries `Ume-Change-Propagation: delayed`: the gateways apply the
change when their cache expires (`Gateway:KeyCacheSeconds` / `Gateway:CatalogCacheSeconds`, 30 s by default). The admin UI
shows this as a notice.

## Authentication (BFF)
- `GET /bff/login?returnUrl=/path` → 302 to OIDC provider (Keycloak in dev, Entra ID/AD FS in prod).
- `POST /bff/logout` → signs out (cookie + OIDC end-session).
- Logout returns `{ redirectUrl }`; navigate to this URL to complete OIDC provider signout.
  The end-session request includes `client_id` and protected state for the local return to `/`.
  Register `/signout-callback-oidc` as an allowed post-logout redirect at the identity provider.
  Tokens are not saved; providers such as Keycloak may therefore require logout confirmation.
- `POST /bff/session/extend` → `{ sessionExpiresAt, sessionLimitAt }`, renews an authenticated session for eight
  hours (but not past `sessionLimitAt`) without losing unsaved forms. At the limit it answers `409`: sign in again.
  Requires the same CSRF headers as other mutations.
- `GET /bff/user` → `{ isAuthenticated, name, email, roles: string[], departmentCodes: string[], sessionExpiresAt, sessionLimitAt }`
  (always 200; `isAuthenticated=false` when not signed in). `sessionLimitAt` is the absolute end of the session.
- Session cookie: `__Host-ume-admin`, HttpOnly, Secure, SameSite=Lax (compatible with OIDC redirects).
  No tokens reach the browser. The cookie only holds a session id; the session itself is stored encrypted in Redis
  (`ume:admin-session:*`), so signing out ends it for every copy of the cookie. A sign-in lasts eight hours, a session at
  most `Admin:MaxSessionHours` (12, 1–24) from sign-in; the UI warns before expiry and can start a new login.
- **CSRF**: every non-GET request must send header `X-XSRF-TOKEN` with the value of the readable cookie
  `XSRF-TOKEN` (set on `GET /bff/user`). Also send `X-Requested-With: XMLHttpRequest`.
- Unauthenticated API calls → 401 (no redirect). Missing role → 403.
- Local configuration: `Oidc:Authority`, `Oidc:ClientId` (default `ume-admin`), optional
  `Oidc:ClientSecret`, `ConnectionStrings:gatewaydb`, `Security:KeyPepper`, optional Redis and
  Data Protection certificate settings shared with the gateway. Production OIDC metadata requires HTTPS.
  Settings are validated at startup: the API does not start without `Oidc:Authority`, with `Admin:RequestsPerMinute`
  out of range, or with a `Gateway:BaseUrl` / `Gateway:OperationsUrl` that is not an absolute http(s) URL.
  `Gateway:BaseUrl` is the public gateway address shown in the catalogue (default `https://localhost:5140`).
- API rate limit: 120 requests/minute per authenticated subject (`Admin:RequestsPerMinute`, 1–10000).
  Exceeded → 429 ProblemDetails with `Retry-After`.
- Suggested Azure AI Foundry prices come from the Azure retail price list of `Admin:AzurePriceRegion` (default `swedencentral`).
- The newest release is read from GitHub (`https://api.github.com/repos/<Admin:ReleaseRepository>/releases/latest`, default
  `thekim1/llmgateway`, no key; the admin API needs outbound HTTPS to `api.github.com`). Set `Admin:CheckForReleases=false`
  (`Admin__CheckForReleases`) where it has no internet access; the running version is still shown.

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
- `AttachmentPolicy`: `Allowed` (default), `ImagesOnly`, `None` (text only)
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
- `GET /api/teams?departmentId=` → `Team[]` where `Team = { id, departmentId, departmentName, name, description, isActive, keyCount, createdAt, requestPolicy: RequestPolicy|null }`
- `POST /api/teams` `{ departmentId, name, description?, requestPolicy? }` → 201 `Team`
- `PUT /api/teams/{id}` `{ name, description?, isActive, requestPolicy? }` → `Team` (`requestPolicy` omitted = unchanged, `{}` clears it)
- `RequestPolicy = { maxOutputTokens?, systemPrompt?, defaults?: object, overrides?: object }`: applied by the gateway to
  every request of the team's keys, after the PII policy and routing rules. `maxOutputTokens` (1–1 000 000) lowers
  `max_tokens` / `max_completion_tokens` / `max_output_tokens` to it, or sets it when the request has none; `systemPrompt`
  (at most 8 000 characters) goes before the request's own system message, `instructions` or `system`; `defaults` are
  request parameters set when the request leaves them out, `overrides` always (at most 20 each). They may not set `model`,
  `messages`, `input`, `prompt`, `system`, `instructions`, `stream`, `stream_options`, `file`, `image`, `mask`, the
  `max_*` token parameters, or the provider tool and storage fields (`tools`, `tool_choice`, `web_search_options`,
  `mcp_servers`, `container`, `store`, `background`, `conversation`, `previous_response_id`), which need the key's own
  *Allow provider tools* / *Allow provider storage* (400). A key's own policy wins over its team's field by field (parameters by name).
- `DELETE /api/teams/{id}` → 204 (409 if it has keys)

## Virtual keys
`VirtualKey = { id, teamId, teamName, departmentId, departmentName, name, description, prefix, status: KeyStatus,
isEnabled, createdAt, createdBy, expiresAt, revokedAt, graceUntil, lastUsedAt, allowedModels: string[],
allowedResidencies: DataResidency[], allowedProviders: string[] (provider names, empty = all; omitted on update = unchanged), canReveal, piiPolicy, attachmentPolicy: AttachmentPolicy, requestsPerMinute, tokensPerMinute, rotatedToKeyId,
allowedIpRanges: string[], allowProviderTools: boolean, allowProviderStorage: boolean, maxConcurrentRequests: number|null,
requestPolicy: RequestPolicy|null }`
(`contentGuardPolicy`: `Off` (default), `Record` or `Block`, see [content guard](content-guard.md); omitted on update = unchanged.
`maxConcurrentRequests`, 1–100 000 or null: requests of the key in progress at once across all gateway instances; more
get 429 `rate_limited`. Live audio sessions have their own limit. `requestPolicy`: see *Organisation*; omitted on update =
unchanged, `{}` clears it. Both are copied to the replacement key on rotation.)
(`allowedIpRanges`: client addresses the key may be used from, single IPs or CIDR networks such as `203.0.113.0/24`, at most
100; empty = any address; omitted on update = unchanged; returned in canonical form, a network with host bits set such as
`10.20.1.5/16` is refused with 400; copied to the replacement key on rotation. Behind a reverse proxy it needs the proxy in
[trusted proxies](#settings). Any key manager may set it.)
(empty `allowedModels` / `allowedResidencies` = all allowed)
(`attachmentPolicy` omitted = `Allowed` on create, unchanged on update; it is copied to the replacement key on rotation)
(`allowProviderTools` / `allowProviderStorage` omitted = `false` on create, unchanged on update; copied on rotation; see
[provider tools and storage](#gateway-data-plane--for-the-developer-portal-snippets))
(`lastUsedAt` has minute resolution: each gateway instance writes it at most once per key and minute)

- `GET /api/keys?teamId=&departmentId=&status=` → `VirtualKey[]`
- `GET /api/keys/{id}` → `VirtualKey`
- `POST /api/keys` `{ teamId, name, description?, expiresAt?, allowedModels, allowedResidencies, allowedProviders?, allowedIpRanges?, piiPolicy, attachmentPolicy?, allowProviderTools?, allowProviderStorage?, requestsPerMinute?, tokensPerMinute?, maxConcurrentRequests?, requestPolicy? }`
  → 201 `{ key: VirtualKey, secret: "ume-sk-…" }` — **secret is shown once, never retrievable again**
- `PUT /api/keys/{id}` `{ name, description?, expiresAt?, allowedModels, allowedResidencies, allowedProviders?, allowedIpRanges?, piiPolicy, attachmentPolicy?, allowProviderTools?, allowProviderStorage?, requestsPerMinute?, tokensPerMinute?, maxConcurrentRequests?, requestPolicy?, isEnabled }` → `VirtualKey`
- `POST /api/keys/{id}/rotate` `{ mode: KeyRotationMode }` → `{ key: VirtualKey, secret, previousKey: VirtualKey }`
- `POST /api/keys/{id}/reveal` `{ purpose: "Reveal" | "Copy" }` → `{ secret }` (gateway-admin only; writes an audit entry `reveal`/`copy` with the actor; 409 for revoked keys and for keys created before the secret was stored, `VirtualKey.canReveal` is false for those)
- `POST /api/keys/{id}/revoke` → `VirtualKey` (immediate, also cancels grace)
- Rotation retains the predecessor's key budgets and spend across the full rotation lineage, including
  the grace-period key. It does not reset the remaining budget. Concurrent conflicting mutations → 409.

## Data API access (gateway-admin)
Keys for the read-only Data API ([data-access.md](data-access.md#data-api-keys)), for integrations that cannot use OAuth client credentials.
`DataApiKey = { id, name, description, prefix, permissions: DataPermission[], status: "Active"|"Expired"|"Revoked", createdAt, createdBy, expiresAt, revokedAt, lastUsedAt }`
(`DataPermission` = `usage.aggregate` | `usage.detail` | `security.read` | `catalog.read`, stored in that order; `lastUsedAt` is written by the Data API at most every 5 minutes per key)

- `GET /api/data-access` → `{ dataApiUrl: string|null, permissions: DataPermission[] }` (`dataApiUrl` from the admin API's `DataApi:PublicUrl`)
- `GET /api/data-access/keys` → `DataApiKey[]` (sorted by name; never the secret or hash)
- `GET /api/data-access/keys/{id}` → `DataApiKey`
- `POST /api/data-access/keys` `{ name, description?, permissions: DataPermission[] (1+), expiresAt? }` → 201 `{ key: DataApiKey, secret: "ume-dk-…" }` — **secret is shown once, only its SHA-256 is stored**
- `PUT /api/data-access/keys/{id}` (same body) → `DataApiKey`; 409 for a revoked key
- `POST /api/data-access/keys/{id}/revoke` → `DataApiKey`; 409 if already revoked. The Data API refuses the key within `DataApi:KeyCacheSeconds` (30 s).
- `DELETE /api/data-access/keys/{id}` → 204; 409 for an active key (revoke it first)
- Names are unique (400 with a field error on `name`); `expiresAt` must be in the future. Every change is audited (entity type `DataApiKey`, actions `create`, `update`, `revoke`, `delete`; details never hold the secret or hash).

## Providers, models, routes
- Plain HTTP is accepted only for on-prem local/test configuration. Production gateway
  invocation requires HTTPS even for on-prem targets and never follows upstream redirects.
  Configure approved TLS endpoints before enabling production providers.
- `Provider = { id, name, displayName, type, baseUrl, authMode, hasCredential, residency, capabilities: ProviderCapabilities[], isEnabled, isDrained, timeoutSeconds, createdAt, deploymentCount }`
  (credential is **write-only**, never returned)
- `GET /api/providers` · `POST /api/providers` `{ name, displayName?, type, baseUrl, authMode, credential?, residency, capabilities, timeoutSeconds, isEnabled }`
- `PUT /api/providers/{id}` same body; `credential`: omitted/null = unchanged, `""` = remove. A stored credential is only
  kept while the base URL's scheme, host and port stay the same: moving the provider elsewhere without a new `credential`
  (or `""`) answers `400` with a field error on `credential`.
- `POST /api/providers/{id}/drain` `{ drained: boolean }` → `Provider` · `DELETE /api/providers/{id}` (409 if models exist)
- `POST /api/providers/{id}/discover-models` → `DiscoveredModel[] = { id, displayName, kind, parameterProfile, contextWindow|null, features: string[], price: { inputPerMillionUsd, cachedInputPerMillionUsd, outputPerMillionUsd }|null, alreadyAdded }`. Calls the provider's `/models` with the stored credential, so it doubles as a connection test (502/504 on failure). Context size, features and price are included only when the provider reports them. Azure OpenAI is unsupported (400). In Production the provider address must be https (400 otherwise), like the gateway; one attempt, 20 s timeout.
- `Price = { inputPerMillionUsd, cachedInputPerMillionUsd, cacheWriteInputPerMillionUsd|null, outputPerMillionUsd, audioPerMinuteUsd, audioInputPerMillionUsd, audioOutputPerMillionUsd, perImageUsd, effectiveFrom }`
  (`audioPerMinuteUsd` is for models billed by duration, such as Whisper and gpt-realtime-translate; `audioInputPerMillionUsd`
  and `audioOutputPerMillionUsd` price the audio part of the tokens of realtime and gpt-4o-transcribe models, 0 = billed
  as text tokens; `perImageUsd` is for image models billed per picture, such as DALL·E; all four omitted on create = 0;
  `cacheWriteInputPerMillionUsd` prices prompt-cache writes (Anthropic `cache_creation_input_tokens`), omitted or null =
  billed at `inputPerMillionUsd`)
- `ProviderCapabilities`: `ChatCompletions`, `Embeddings`, `Responses`, `AnthropicMessages`, `Streaming`, `AudioTranscriptions`
  (speech to text), `Realtime` (live audio over WebSocket), `Images` (image generation and editing). `ModelKind` (models and
  routes): `Chat`, `Embedding`, `Transcription`, `Realtime`, `SpeechTranslation`, `Image`. Discovery adds Whisper and `*-transcribe` models as `Transcription`,
  `*realtime-translate*` as `SpeechTranslation`, other `*realtime*` models as `Realtime` and `gpt-image*`, `dall-e*`,
  `flux*`, `imagen*` and `stable-diffusion*` models as `Image`; speech synthesis, video and chat-with-audio models are still left out.
- `baseUrl` may carry query parameters (e.g. `?api-version=…` for Foundry Voice Live); they are kept after the path the
  gateway appends. Parameters that look like credentials (`*key*`, `*token*`, `*secret*`, `*auth*`, `sig`, `code`) are refused.
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
- `RoutingRule = { id, name, description, isEnabled, isTemplate, priority, scope: "Global"|"Department"|"Team"|"VirtualKey", scopeIds: Guid[],
  owners: [{ id, name|null }], isOrphaned, condition, chain, targets: [{ model, weight }], fallbacks: string[], missingModels: string[],
  validationErrors: [{ message, position|null, length|null }], createdAt, updatedAt }`
  - `name` is the rule's title (the UI labels it *Title*); `description` a short description.
  - `scopeIds`/`owners`: the keys, teams or departments (all of the kind `scope` names) the rule is **shared by**; empty for a global rule. An owner's
    `name` is `null` when it no longer exists.
  - `isOrphaned`: none of the rule's owners exists any more. Such a rule never applies and cannot be enabled until reassigned.
  - `isTemplate`: a shipped example rule (seeded disabled). Editing, enabling or reassigning it makes it an ordinary rule (`isTemplate: false`).
  - `missingModels`: targets or fallbacks that are neither a route alias nor a model. A disabled rule may have them; it cannot be enabled until they exist.
  - `validationErrors`: why the gateway ignores an enabled rule (empty when valid).
- `GET /api/routing-rules?scope=&scopeId=&orphaned=` (ordered by scope, then priority; `scopeId` returns the rules whose owners include it) · `GET /api/routing-rules/{id}`
- `POST /api/routing-rules` · `PUT /api/routing-rules/{id}` body
  `{ name, description?, scope, scopeIds?: Guid[] (≤1000), isEnabled, priority, condition?, chain, targets: [{ model, weight (1..1000000) }] (1..20), fallbacks?: string[] }`
  → `RoutingRule`. `model` is a route alias or model name. Validation → 400 with field errors (`condition` messages include the character position,
  `targets`, `fallbacks`, `scopeIds`). An **enabled** rule needs at least one owner (all existing), a non-chained enabled rule may only target existing
  names and its fallbacks must exist; a **disabled** rule may name models that do not exist yet and may keep an owner that has been removed (new owners must exist).
  Duplicate owner ids are dropped. Names are unique among global rules, and among rules of one scope that share an owner (409, case-insensitive).
- `PUT /api/routing-rules/{id}/enabled` `{ isEnabled }` → `RoutingRule`. The on/off switch. Turning a rule on checks it as a save would (owners and
  models must exist); otherwise **400** with `detail` "Regeln kan inte aktiveras. …" and the field `errors`. Audited as `update`.
- `DELETE /api/routing-rules/{id}` → 204
- `POST /api/routing-rules/{id}/reassign` `{ scope, scopeIds?: Guid[], enable = true }` → `RoutingRule`. Attaches the rule to new teams, departments or keys (at least one, all existing) or makes it global and, by default, enables it (then its models must exist).
- `POST /api/routing-rules/reorder` `{ scope, ruleIds: Guid[] }` → `RoutingRule[]`. Sets priorities 0, 10, 20, … in the given order; every id must belong to that scope (the order is kept per scope, since rules can be shared by several owners).
- `POST /api/routing-rules/validate` `{ condition }` → `{ valid, errors: [{ position, length, message }], variables: string[], available: [{ name, type }] }` (live validation while typing).
- `POST /api/routing-rules/test` (dry run) `{ model, endpoint?, headers?, params?, keyId?, teamId?, departmentId?, budgetUsed?, tokensUsed?, piiDetected?, promptTokens?, seed? }`
  → `{ matched, primaryModel, models: [{ name, exists, type, kind, enabled }], applied: [{ ruleId, name, fromModel, toModel }], chainLimitReached,
  evaluation: [{ ruleId, name, scope, priority, chainStep, model, outcome: "Matched"|"NotMatched"|"Skipped", trace: [{ text, leftValue, result|null }] }],
  ignoredRules, note }`. Evaluates the **stored, enabled** rules. `keyId` fills in team, department and key name (and the key's rotation lineage);
  usage values that are not supplied are unknown, so conditions using them do not match. `seed` makes weighted choices repeatable.
- **Deleting a team or department that has rules.** `DELETE /api/teams/{id}` and `DELETE /api/departments/{id}` take an optional query parameter
  `routingRules=delete|deactivate`. The team/department is removed from every rule shared with other owners (audited as `update`; those rules keep
  applying to the rest). If it is the **only** owner of some rules and the parameter is missing, the call fails with **409** and
  `{ code: "routing_rules_scoped", choices: ["delete","deactivate"], rules: [{ id, name, isEnabled }], detail }` (only those rules) so the client can ask the user.
  `delete` removes them; `deactivate` disables them with no owners and keeps everything else, so they can be attached to new owners with
  `reassign`. The choice and the delete are saved in one transaction and audited per rule. Any other value → 400.
- **Names in use.** A route alias or model that a rule uses as target or fallback cannot be deleted or renamed (409 naming the rule). Conditions that compare `model` with a name are not checked.
- Key rotation: a rule scoped to a key keeps applying to the key that replaced it, like budgets.

## Budgets & alerts
- Cost budgets are in USD, the currency provider prices are in, so what a budget allows does not change with the exchange
  rate; spend is the sum of the usage records' `costUsd`. **Token quotas** (`unit: "Tokens"`) count input plus output
  tokens instead (`limitTokens`, `spentTokens`; their USD fields are 0), optionally only of requests served by providers of
  one `residency` (`OnPrem`, `Eu`, `External`; null = all): the way to share on-prem capacity, whose models cost nothing.
  One cost budget per owner and period, and one token quota per owner, period and residency (409 otherwise).
- `Budget = { id, scope, scopeId, scopeName, limitUsd, period, alertThresholds: number[], isActive, periodStart, periodEnd, spentUsd, percentUsed, unit: "Usd"|"Tokens", limitTokens, spentTokens, residency: DataResidency|null }`
- `GET /api/budgets?scope=&scopeId=` · `POST /api/budgets` `{ scope, scopeId, limitUsd, period, alertThresholds, isActive, unit?, limitTokens?, residency? }` · `PUT /api/budgets/{id}` · `DELETE /api/budgets/{id}`
  (`unit` omitted = `Usd`; a token quota needs `limitTokens` > 0; `residency` only on token quotas, 400 otherwise)
- `GET /api/alerts?acknowledged=false` → `[{ id, budgetId, scope, scopeName, thresholdPercent, spentUsd, limitUsd, periodStart, timestamp, acknowledged, spentTokens|null, limitTokens|null }]`
  (token quota alerts have `spentTokens`/`limitTokens` and USD amounts of 0; the alert webhook adds `unit`, `spentTokens`, `limitTokens`)
- `POST /api/alerts/{id}/acknowledge` → 204
- `GET /api/settings/exchange-rate` → `{ currency: "USD", sekPerUnit, effectiveFrom }`, or 200 with an empty body when no rate has been entered · `PUT /api/settings/exchange-rate` `{ sekPerUnit }`.
  Until a rate is in effect the gateway prices with `Gateway:FallbackSekPerUsd` (default 10, 0.01–1000), logs a warning and
  reports its `exchange-rate` readiness check (and so the gateway component in `/api/ops/health`) as Degraded.
- `GET /api/settings/retention` *(gateway-admin)* → `{ usageRetentionDays, auditRetentionDays (null = kept for ever), usageRetainedFrom,
  nextUsageCutoff, nextAuditCutoff, enabled, nextRunAt, running, lastRunAt, lastRunDeleted, lastRunError }` · `PUT /api/settings/retention`
  `{ usageRetentionDays, auditRetentionDays }` (90–3 650; audited as `Settings`) · `POST /api/settings/retention/run` → 202 with the state
  (409 when a run is going; audited as `run`). See *Data retention* in [the runbook](runbook.md). `usageRetainedFrom` is the oldest instant
  usage records cover; `GET /api/usage/summary` returns `monthlyTotalsBefore` when its period reaches before it (usage before that instant
  comes from whole-month totals; `groupBy=day` shows those months as `yyyy-MM`). The CSV export and budget spend use the totals the same way.
- <a id="settings"></a>`GET /api/settings/network` *(gateway-admin)* → `{ trustedProxies: string[], updatedAt, observed: { remoteAddress, forwardedFor } }`
  · `PUT /api/settings/network` `{ trustedProxies: string[] }` *(gateway-admin)*. Trusted reverse proxies: the gateway takes the
  client address from `X-Forwarded-For` only on connections from these addresses (single IPs or CIDR networks, at most 100,
  canonicalised; `/0` networks are refused). `observed` is the connection the admin API saw for this request, to help find
  the proxy's address. Saving is audited (`Settings`) and publishes a configuration change, so the gateways apply it within
  seconds. See *Client addresses and trusted proxies* in [the runbook](runbook.md).
- `GET /api/settings/version` *(gateway-admin)* → `{ version, developmentBuild, checkEnabled, latest: { version, name, url,
  publishedAt } | null, updateAvailable, checkedAt, checkFailed }`. `version` is the running admin API's (`0.0.0-local` for a local
  build: `developmentBuild`, never `updateAvailable`). `latest` is the newest published GitHub release (drafts and pre-releases
  excluded), checked at most every 6 hours (after a failure, again within 30 minutes; `checkFailed` then keeps any earlier result);
  null when the check is off or no release exists. `url` is always a `github.com` link. Browsers never call GitHub.
- `GET /api/settings/request-log` *(gateway-admin)* → `{ active, until, updatedAt }` · `PUT /api/settings/request-log`
  `{ logAllRequests, noTimeLimit }` *(gateway-admin)*. Whether the gateways log successful requests too (normally only refused,
  failed and cancelled ones; every request is a usage record either way). Switched on, it lasts 24 hours unless `noTimeLimit` is
  true; `until` is when it switches itself off (null with `active` = no time limit; `active` is false once it has expired).
  Saving is audited (`Settings`) and publishes a configuration change; the gateways check the end time on every request.
- Budget alerts are stored with the usage batch that crossed the threshold. With `Gateway:AlertWebhookUrl` set, the gateway
  also POSTs each alert once (metadata only, 5 s timeout, no retries) from its own queue of 1 000; when that queue is
  full the alert is stored but not sent (a warning is logged). Body: `{ type: "budget_threshold", scope, scopeId,
  thresholdPercent, spentUsd, limitUsd, spentSek, limitSek, periodStart }`; the SEK amounts are the USD amounts at the
  exchange rate when the alert was raised, kept for existing receivers until SEK is removed.

## Usage (metadata only – never prompt/response content)
- `GET /api/usage/summary?from=&to=&groupBy=department|team|key|model|provider|day&departmentId=&teamId=`
  → `{ from, to, totalCostUsd, totalRequests, totalInputTokens, totalOutputTokens, totalErrors, rows: [{ key, label, requests, inputTokens, outputTokens, costUsd, errors, fallbacks }] }`
- `GET /api/usage/metrics?from=&to=&bucket=auto|hour|day&departmentId=&teamId=&keyId=&model=&provider=&outcome=&endpoint=` (any role, own departments)
  → `{ from, to, bucket, timeZone, recordsFrom, totals, buckets: [...], models: [...] }`, the Usage dashboard. Cost is in **USD**.
  - `bucket`: `auto` (default) is `hour` for periods up to two days, else `day`. Hours are cut in UTC, days at Europe/Stockholm midnight
    (`timeZone`). `hour` allows at most 7 days; the period is at most one year (default: the last 30 days). Every bucket in the
    period is returned, empty ones with zeros and `null` timings, so the series have no gaps.
  - Filters: `model` is the requested model (alias or deployment name), `provider` the provider account name, `outcome` a
    `RequestOutcome`, `endpoint` a `GatewayEndpoint` name. Anything else is 400.
  - `totals` and each bucket: `requests`, `successes`, `providerErrors`, `refused` (BudgetExceeded, RateLimited, PiiBlocked,
    Rejected), `cancelled`, `fallbacks` (requests with at least one fallback), `costUsd`, `inputTokens`, `cachedInputTokens`,
    `outputTokens`, `latency` and `overhead` (each `{ avg, p50, p90, p95, p99 }` in ms) and `tokensPerSecond`. `totals` also
    has `cacheHitRate` (cached ÷ input tokens) and `firstToken: { avg, p50, p95 }`; a bucket has `firstTokenAvgMs` and `firstTokenP95Ms`.
  - Timings: `latency` leaves out live audio sessions and requests no provider handled; `overhead` is `latencyMs − upstreamMs`
    for records that have it; time to first token covers streamed answers; `tokensPerSecond` is output tokens ÷ provider time
    over successful requests with output. Percentiles are nearest-rank (`percentile_disc`), over the whole period for `totals`.
  - `models`: `[{ model, requests, costUsd, requestsPerBucket[], costUsdPerBucket[] }]`, the 8 requested models with the most
    requests, then one entry with `model: null` for all others together; the arrays line up with `buckets`.
  - `recordsFrom` is set when the period starts before the oldest kept usage record (data retention): monthly totals have no
    timings or days, so the figures cover only `recordsFrom`..`to`.
- `GET /api/usage/requests?from=&to=&keyId=&teamId=&departmentId=&outcome=&page=1&pageSize=50`
  → `{ items: UsageRequest[], total }` where `UsageRequest = { requestId, timestamp, keyPrefix, keyName, teamName, departmentName, endpoint, requestedModel, providerName, upstreamModel, inputTokens, cachedInputTokens, outputTokens, latencyMs, statusCode, outcome, fallbackCount, streamed, piiActionApplied, piiCategories, errorCode, routingRuleId, routingRuleName, costUsd, upstreamMs, timeToFirstTokenMs }` (`routingRuleName`: the routing rule that decided where the request went, `null` when none applied; `upstreamMs`: time spent waiting on providers, failed fallback attempts and a streamed answer included, `null` when no provider was called and for live audio sessions; `timeToFirstTokenMs`: streamed answers only)
- `GET /api/usage/requests/{requestId}` → `UsageRequest` (lookup by `x-request-id` from gateway response)
- `GET /api/usage/pii-incidents?from=&to=&keyId=&teamId=&departmentId=&category=&action=&page=1&pageSize=50` (any role, own departments)
  → `{ from, to, items: PiiIncident[], total, totalIncidents, categories: [{ category, requests, occurrences }], actions: [{ action, requests }] }`
  where `PiiIncident = { requestId, timestamp, keyId, keyPrefix, keyName, teamId, teamName, departmentId, departmentName, endpoint, requestedModel, providerName, action, categories: [{ category, count }], outcome, statusCode, routingRuleName }`.
  An incident is a usage record with PII found (`piiCategories` set), newest first; this includes policy `Allow` (detected and sent on).
  `category` is `Personnummer|Samordningsnummer|Email|Phone|Iban`, `action` is `Allow|Redact|Block|RerouteToOnPrem` (case-insensitive;
  anything else is 400). `items`/`total` honour every filter; `categories`, `actions` and `totalIncidents` cover the period and the
  department/team/key filters only. Default period: the last 30 days, at most one year. Keys with PII policy `Off` are not scanned and have no incidents.
- `GET /api/usage/export.csv?from=&to=` → CSV: ansvarskod, förvaltning, team, nyckel, requests, tokens, kostnad USD.
  Semicolon-separated; cells are quoted only when they contain a semicolon, a quote or a line break. A text cell a spreadsheet would run as
  a formula (starting with a tab or line break, or with `=`, `+`, `-` or `@` after any leading spaces) is prefixed with `'`;
  numbers such as `-1.5` are left as they are. The Data API's CSV (comma-separated) uses the same rule.

## Catalogue (developer portal, any authenticated user)
- `GET /api/catalog` → `{ gatewayBaseUrl, routes: [{ name, description, kind, residencies: DataResidency[], capabilities: ProviderCapabilities[], inputUsdPerMillion, outputUsdPerMillion }], models: [{ name, kind, residency, capabilities, inputUsdPerMillion, outputUsdPerMillion }] }`

## Operations (gateway-admin)
- `GET /api/ops/health` → `{ checkedAt, components: [{ name, status: "Healthy"|"Degraded"|"Unhealthy", description }], versions: { adminApi, gateway: string|null, schema }, usageWriter: { queueDepth, capacity, inFlightRecords, lastWriteAt: string|null, consecutiveFailures, journaledBatches }|null, providers: [{ id, name, type, residency, isEnabled, isDrained, circuitState, requests24h, errorRate24h, providerErrorRate24h, fallbackRate24h, p50LatencyMs, p95LatencyMs, status, statusReason, recentRequests, recentProviderErrors }] }` (providers sorted by name; the latency percentiles leave out live audio sessions, whose `latencyMs` is their length)
  - `errorRate24h`: share of the last 24 h's requests that did not succeed (client errors and cancellations included). `providerErrorRate24h`: only *provider errors*, i.e. outcome `ProviderError` with status ≥ 500 and an error code other than `internal_error` (upstream 5xx, timeouts, broken streams).
  - `status`: `"Healthy"|"Degraded"|"Unhealthy"|"CircuitOpen"|"NoRecentTraffic"|"Disabled"`, computed on every call: `Disabled` if the provider is disabled, else `CircuitOpen` if its circuit is open, else from the last hour (`recentRequests`, `recentProviderErrors`): no requests → `NoRecentTraffic`; fewer than 10 → `Healthy` (too few to judge); otherwise provider error rate ≥ 50 % → `Unhealthy`, ≥ 5 % → `Degraded`, else `Healthy`. `statusReason` is an English sentence for the UI, e.g. `"4 of 40 requests failed at the provider in the last hour (10 %)."`.
- `GET /api/ops/providers/stats?from=&to=&bucket=hour|day` → `{ from, to, bucket: "Hour"|"Day", timeZone, providers: [{ providerId, name, exists, requests, providerErrors, availability, fallbackRate, p50LatencyMs, p95LatencyMs }], buckets: [{ providerId, start, requests, providerErrors, errorRate, fallbacks, p50LatencyMs, p95LatencyMs }] }`
  - `bucket` (case-insensitive) defaults to `hour`; `to` defaults to now and `from` to 24 hours (hour) or 30 days (day) before `to`. `from` is moved back to the start of its bucket and returned. Hour buckets are UTC hours (Stockholm's offset is whole hours, so they are also local hours) and allow at most 7 days; day buckets are Europe/Stockholm days and allow at most 90. Otherwise 400 (`"Timvis indelning tillåter högst 7 dygn. Välj day för längre perioder."`, `"Välj ett tidsintervall på högst 90 dygn."`, `"Tidsintervallets slut måste ligga efter dess början."`, `"Indelningen måste vara hour eller day."`).
  - `buckets` holds only buckets with traffic (no row means no requests). `providers` is the whole period, most stable first: `availability` (1 − provider error rate) descending, then `p95LatencyMs` ascending, then requests. A provider deleted since has `exists: false` and the name recorded with its requests. Provider errors are counted as for `/api/ops/health`; percentiles leave out live audio sessions; `fallbacks`/`fallbackRate` count requests this provider answered after an earlier candidate failed.
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
- `POST /api/ops/cache/invalidate-keys` → 204 (audited as `invalidate` on `Keys`; the cache invalidations that
  ordinary changes publish are not audited separately)
- `GET /api/ops/config/export` → JSON document (providers without credentials, models, prices, routes, global routing rules)
- `POST /api/ops/config/import` (same JSON) → `{ created, updated, skipped }`
- Config schema: `{ schemaVersion: 1, providers: [{ configuration: ProviderRequest (without credential),
  credentialEnvironment?: "UME_PROVIDER_..." }], models: [{ providerName, configuration: ModelRequest,
  prices: Price[] }], routes: [{ name, description, kind, isEnabled,
  targets: [{ modelName, priority, weight }] }], routingRules?: [{ name, description?, isEnabled, priority, condition?, chain,
  targets: [{ model, weight }], fallbacks?: string[] }] }`. Only **global** routing rules are transferred (key/team/department ids differ between
  environments), and not untouched templates (every installation has them); rules are matched by name, created or updated, never deleted by import, and each is validated like one saved through the API (an invalid rule fails the whole import).
  Documents without `routingRules` (older exports) still import. Changing the kind of a model that a route uses → 409
  "Modelltypen kan inte ändras när modellen används i en rutt." (same as `PUT /api/models/{id}`).
  References use stable names; database IDs in model configuration are ignored on import.
  Import is transactional, rejects plaintext credentials, preserves existing encrypted credentials when
  a reference is omitted, and never overwrites price history. Environment references must already exist
  in the Admin API process. Export never contains secret values or encrypted credentials.
- `GET /api/audit?page=&pageSize=&entityType=` → `{ items: [{ id, timestamp, actor, action, entityType, entityId, details, actorName,
  actorRoles, actorDepartments, sourceAddress, sessionId, requestId, userAgent }], total }`. `actor` is the OIDC subject; the
  other actor fields are recorded with each entry: name or e-mail, roles and department codes at the time, the client address
  behind the trusted proxies (`Admin:AuditSourceAddress`: `Full` by default, `Truncated` or `None`), a random session id
  given at sign-in (never the cookie), the request's trace id and the user agent (200 characters). Entries written before
  have them empty. `GET /api/ops/config/export` and `GET /api/usage/export.csv` write an entry with action `export`
  (entity `Config` or `Usage`, with counts or the period and row count).
  - `details` is JSON `{ before, after }` (camelCase, enums as names) with snapshots of the entity's own stored settings,
    never a secret: no derived or volatile values such as `deploymentCount`, `createdAt`, `currentPrice`, `providerName`,
    key `status` or `lastUsedAt`. For a rotation `after` is `{ previousKey, key }`, for a reveal/copy the key's name and
    prefix, for a config import the counts `{ created, updated, skipped }`.

## Gateway (data plane) – for the developer portal snippets
- Base URL: `{gatewayBaseUrl}/v1`. Auth: `Authorization: Bearer ume-sk-…` (also `x-api-key` / `api-key`).
- `POST /v1/chat/completions`, `POST /v1/embeddings`, `GET /v1/models`, `POST /v1/responses`, `POST /v1/messages` (Anthropic format).
- `POST /v1/audio/transcriptions` and `POST /v1/audio/translations` (speech to text, OpenAI format): `multipart/form-data`
  with the recording in `file` and the text fields `model` (a `Transcription` alias or model), `language`, `prompt`,
  `response_format` (`json`, `text`, `srt`, `vtt`, `verbose_json`), `temperature`, `stream`, `timestamp_granularities[]` …;
  every field except `file` and `model` is passed on unchanged. The answer comes back in the provider's format and
  content type. Only providers with the `AudioTranscriptions` capability are used. Uploads are limited to
  `Gateway:MaxAudioRequestBodyBytes` (26 MB) and held in memory, never on disk. The PII policy scans `prompt`; the
  recording itself counts as an audio attachment, so keys set to `ImagesOnly` or `None` get `attachment_not_allowed`.
  Billing: provider-reported duration (`usage.seconds` or the `duration` of `verbose_json`) × `audioPerMinuteUsd`, plus
  reported tokens × token prices (gpt-4o-transcribe); whatever is not reported is estimated from the file (exact for
  WAV and FLAC, from the bitrate for MP3, 32 kbps otherwise). Usage records carry `audioSeconds`.
- `POST /v1/images/generations` (JSON: `model` an `Image` alias or model, `prompt`, `n`, `size`, `quality`, `response_format` …) and
  `POST /v1/images/edits` (`multipart/form-data`: the pictures in `image` or `image[]`, at most 16, an optional `mask`, and the text
  fields `model`, `prompt`, `n`, `size` …): OpenAI format; every field except the files is passed on unchanged and the answer comes
  back as the provider sent it (`data[]` with `b64_json` or `url`, `usage` for token-billed models). Only providers with the `Images`
  capability are used. Edits are limited to `Gateway:MaxImageRequestBodyBytes` (50 MB for all pictures together) and held in memory,
  never on disk. `stream` is refused with `400 invalid_request`. The PII policy scans the text fields; the pictures count as image
  attachments, so keys set to `None` get `attachment_not_allowed` on edits. Billing: the number of pictures in `data` ×
  `perImageUsd`, plus reported tokens × token prices (gpt-image-1).
- Live audio: see [below](#live-audio-realtime).
- Response headers: `x-request-id`, `x-ume-provider`, `x-ume-model`, `x-ume-fallbacks`, `x-ume-cost-usd`,
  `x-ratelimit-limit-requests`, `x-ratelimit-remaining-requests`, `x-ume-budget-remaining-usd`,
  `x-ume-rule` (id(s) of the routing rule(s) that applied; see [routing rules](routing-rules.md)).
  `x-ume-cost-sek` and `x-ume-budget-remaining-sek` carry the same amounts in SEK at the current exchange rate; they are
  for display and will be removed with SEK.
- Error body (OpenAI style): `{ "error": { "message", "type", "code", "request_id", "doc_url" } }` (English `message`;
  `doc_url` is null when `Gateway:PublishApiReference` is off and `Gateway:DocsUrl` points at the gateway itself) with codes
  `invalid_api_key`, `key_expired`, `key_revoked`, `key_disabled`, `address_not_allowed` (403: the key is restricted to other
  client addresses), `model_not_allowed`, `model_not_found`, `model_ambiguous`, `budget_exceeded`,
  `rate_limited`, `pii_blocked`, `attachment_not_allowed`, `tool_not_allowed`, `storage_not_allowed`, `no_eligible_provider`,
  `all_providers_failed`, `invalid_request` (also for a body that is not valid JSON or repeats a property, such as two
  `model` fields),
  `internal_error` (500: an unexpected failure inside the gateway after the request was accepted; it is recorded as usage
  with outcome `ProviderError`, nothing is charged and the budget reservation is released; quote the `request_id`).
- Model names: a model is addressed by its public name (the plain name while one provider has it) or `<provider>/<model>`
  (always). A plain name several providers share, with no route of that name, is refused with 400 `model_ambiguous`
  listing the `<provider>/<model>` choices; the gateway never picks a provider by itself. `GET /api/models` returns each
  model's `publicName`; model names are unique per provider (409 otherwise).
- Fallback and circuit breaker: only to what an admin set up (a route's targets, a routing rule's targets and fallbacks;
  never to another provider's model of the same name). The next one is tried after a transient failure (408, 409, 429, 5xx, timeout,
  connection error, invalid JSON) or a provider configuration failure (upstream 401/403/404, `https_required`,
  `credential_unavailable`, `endpoint_not_supported`). Only transient failures count toward the provider's circuit
  breaker. Other client errors (e.g. 400) are returned to the client, see below.
- `GET /v1/models` lists only aliases and models the key may use and that some endpoint of their kind can reach, by the
  same rule as routing: model and provider enabled and not drained, a provider capability for that endpoint, and the
  key's provider and residency allow-lists.
- Outgoing requests to providers carry `User-Agent: Ume-LlmGateway/<version>`.
  A provider-side refusal is returned with the provider's status, but only its `message`, `type`, `code` and `param`, in
  the client's error shape (OpenAI, or Anthropic on `/v1/messages`) with the gateway's `request_id`; the rest of the
  provider's body (inner errors, deployment or organisation names) is dropped, and a body that is not JSON becomes
  *The provider rejected the request.* (`provider_rejected`). One the gateway raises itself is
  `unsupported_content` (400): a Chat Completions request to a Claude model contains a part the Messages API cannot
  take (audio, video, an OpenAI `file_id`, or a file other than PDF or plain text). An audio upload with no `file`, or
  one that is not `multipart/form-data`, gets `invalid_request` (400) or `unsupported_media_type` (415); one over the
  limit gets `request_too_large` (413).

### Live audio (realtime)
WebSocket endpoints speaking the OpenAI Realtime protocol (JSON events, audio as base64), relayed to the provider:
- `GET /v1/realtime?model=<alias>`: a `Transcription` alias gives a live transcription session, a `Realtime` alias a
  realtime session. Other query parameters are passed on (e.g. `intent=transcription` for OpenAI's transcription
  sessions with gpt-4o-transcribe, in which case the model is not put in the URL), except credentials and `model`.
- `GET /v1/realtime/translations?model=<alias>`: live interpreting with a `SpeechTranslation` alias.
- Upstream URL: the provider's base URL + `/realtime` or `/realtime/translations`, `ws(s)`, with `model=<upstream model>`.
  Only providers with the `Realtime` capability are used. The `OpenAI-Beta` header is passed on; the subprotocol
  `realtime` is accepted.
- Authentication as for HTTP (`Authorization: Bearer …`), so server-side clients only. Everything the HTTP pipeline
  checks is checked before the upgrade, and refusals are ordinary HTTP errors: `invalid_request` (400: not a WebSocket
  request, no `model`, or a model of the wrong kind), `attachment_not_allowed` (the key's `attachmentPolicy` is not
  `Allowed`), `rate_limited` (429: per-minute limit, or the key already has `Gateway:Realtime:MaxSessionsPerKey` open
  sessions), `budget_exceeded` (402), `all_providers_failed` (502/504, after trying each candidate).
- During the session the gateway sends OpenAI-style `error` events `{ type: "error", error: { type, code, message,
  event_id } }` (`event_id` is the client event refused): `model_not_allowed` (an input transcription model that is
  not a `Transcription` alias/model of the session's provider, or whose provider is unavailable or outside the key's
  provider and residency allow-lists), `pii_blocked`, `tool_not_allowed` (an `mcp` tool in `session.update` or
  `response.create` on a key without provider tools), `invalid_request` (binary frame, or a session or conversation event
  that is not valid JSON or repeats a property; it is not passed on),
  `request_too_large` (event over `MaxMessageBytes`; the session is closed with 1009). It ends the session with an
  error event and a close frame on `budget_exceeded` (close 1008; the message names the exhausted budget like the HTTP 402), `session_time_limit` (1000),
  `gateway_shutting_down` (1001) and `provider_disconnected` (1001).
- Usage: one record per session; `streamed` is true, `latencyMs` is the session length, `audioSeconds` the audio
  streamed (provider-reported duration when given, otherwise counted from the appended audio at the session's input
  format), tokens the sum of the provider's usage events. Outcomes: `Success` (closed by either side, time limit or
  shutdown, with `errorCode` `session_time_limit` / `gateway_shutting_down`), `ClientCancelled` (connection dropped),
  `ProviderError` (`stream_interrupted`, or `internal_error` with the usage so far billed), `BudgetExceeded`.
- Settings (`Gateway:Realtime:`): `MaxSessionsPerKey` (20, 0 = no limit; needs the Redis ACL commands `zadd zrem zcard
  zremrangebyscore`, otherwise the limit is not enforced and a warning is logged), `MaxSessionMinutes` (120),
  `BudgetCheckSeconds` (30), `ReserveMinutes` (5), `MaxMessageBytes` (4 MB).

**Attached files.** Files travel inline in the JSON body as content parts; the gateway has no upload endpoint.
The key's `attachmentPolicy` is checked right after the body is parsed, before rate limiting and the PII policy:

| Kind | Chat Completions | Responses | Anthropic Messages |
|---|---|---|---|
| Image | `image_url` | `input_image` | `image` |
| Document | `file` (`file_data` or `file_id`) | `input_file` (`file_data`, `file_id` or `file_url`) | `document`, `container_upload` |
| Audio | `input_audio` | — | — |
| Video | `video_url` | `input_video` | — |

Parts nested inside other content (for example an Anthropic `tool_result`) are found too. `ImagesOnly` refuses
documents, audio and video; `None` refuses all four. A refused request gets 400 `attachment_not_allowed` and is recorded
with outcome `Rejected`. The PII policy scans text only, never file contents, so a key that must not send personal
data to a provider should use `None`. It cannot stop a client that extracts a file's text itself and sends it as an
ordinary message (common in chat front-ends); the PII policy is what covers that text.

**Provider tools and storage.** Checked after the attachment policy, on Chat Completions, Responses and Anthropic Messages.
Both key settings are off by default:

- `allowProviderTools`: tools the provider runs itself send the conversation to third parties or the internet. Without it
  only client tools are accepted: `function` and `custom` (OpenAI), and Anthropic tools without a type, `custom`, and the
  client-executed `bash_*`, `text_editor_*`, `computer_*` and `memory_*`. Anything else in `tools` (`web_search`,
  `file_search`, `code_interpreter`, `mcp`, `image_generation`, Anthropic `web_search_*`, `web_fetch_*`, `code_execution_*`,
  …), Chat Completions `web_search_options`, and Anthropic `mcp_servers` or `container` get 400 `tool_not_allowed`.
- `allowProviderStorage`: `store: true`, Responses `background: true` and Responses `conversation` keep the conversation at
  the provider; without it they get 400 `storage_not_allowed`, and a Responses request without `store` is sent with
  `store: false` (the Responses API stores by default). `previous_response_id` then only finds responses the provider kept.

Both refusals are recorded with outcome `Rejected` and logged as `gateway.request.refused`.

**Key budgets.** A key can have one budget per period (`Hourly`, `Daily`, `Weekly`, `Monthly`, `Quarterly`, `Yearly`) via `POST /api/budgets` with `scope: "VirtualKey"`; a second budget for the same owner and period returns 409. The gateway enforces every applicable budget (key, team, department) and rejects with 402 when any is exhausted. Hourly windows are aligned to the clock hour. `requestsPerMinute` and `tokensPerMinute` on the key are enforced over a sliding minute: this minute's count plus the previous minute's, weighted by how much of it the last 60 seconds still cover, so a client cannot send twice its limit across a minute boundary (429 `rate_limited` with `Retry-After` when one more fits; token usage is counted after each response; refused requests are not counted).
