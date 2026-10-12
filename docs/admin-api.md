# Admin API contract (control plane)

Base path: same origin as the admin UI (BFF pattern). JSON uses **camelCase**, enums are serialised as
**strings** (e.g. `"RevokeImmediately"`), timestamps are ISO-8601 with offset, money is SEK unless the field
name says `Usd`. Errors use RFC 9457 `application/problem+json`:
`{ "type", "title", "status", "detail", "errors": { "field": ["message"] } }` (Swedish messages).
A malformed request (unreadable body) → 400; an unexpected failure → 500 with the detail "Ett internt fel inträffade."
(the exception is only logged, by type). Every response, error responses included, carries `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY` and `Referrer-Policy: no-referrer`.

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
  Settings are validated at startup: the API does not start without `Oidc:Authority`, with `Admin:RequestsPerMinute`
  out of range, or with a `Gateway:BaseUrl` / `Gateway:OperationsUrl` that is not an absolute http(s) URL.
  `Gateway:BaseUrl` is the public gateway address shown in the catalogue (default `https://localhost:5140`).
- API rate limit: 120 requests/minute per authenticated subject (`Admin:RequestsPerMinute`, 1–10000).
  Exceeded → 429 ProblemDetails with `Retry-After`.
- Suggested Azure AI Foundry prices come from the Azure retail price list of `Admin:AzurePriceRegion` (default `swedencentral`).

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
- `McpAuthType`: `None`, `StaticHeaders`, `OAuthClientCredentials`; `McpServerStatus`: `Healthy`, `Degraded`, `Unavailable`
- `McpServerCapabilities` (array of): `Tools`, `Resources`, `Prompts`, `Completions`
- `McpToolApprovalState`: `Pending`, `Approved`, `Rejected`, `Changed`; `McpApprovalPolicy`: `Always`, `Never`, `NeverForReadOnly`

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
allowedResidencies: DataResidency[], allowedProviders: string[] (provider names, empty = all; omitted on update = unchanged), canReveal, piiPolicy, attachmentPolicy: AttachmentPolicy, requestsPerMinute, tokensPerMinute, mcpCallsPerMinute, rotatedToKeyId }`
(empty `allowedModels` / `allowedResidencies` = all allowed)
(`attachmentPolicy` omitted = `Allowed` on create, unchanged on update; it is copied to the replacement key on rotation)
(`lastUsedAt` has minute resolution: each gateway instance writes it at most once per key and minute)

- `GET /api/keys?teamId=&departmentId=&status=` → `VirtualKey[]`
- `GET /api/keys/{id}` → `VirtualKey`
- `POST /api/keys` `{ teamId, name, description?, expiresAt?, allowedModels, allowedResidencies, piiPolicy, attachmentPolicy?, requestsPerMinute?, tokensPerMinute?, mcpCallsPerMinute? }`
  → 201 `{ key: VirtualKey, secret: "ume-sk-…" }` — **secret is shown once, never retrievable again**
- `PUT /api/keys/{id}` `{ name, description?, expiresAt?, allowedModels, allowedResidencies, piiPolicy, attachmentPolicy?, requestsPerMinute?, tokensPerMinute?, mcpCallsPerMinute?, isEnabled }` → `VirtualKey`
  (`mcpCallsPerMinute`: MCP calls the key may send to servers per minute, null = no limit; like `requestsPerMinute`, omitted = no limit)
- `POST /api/keys/{id}/rotate` `{ mode: KeyRotationMode }` → `{ key: VirtualKey, secret, previousKey: VirtualKey }`
- `POST /api/keys/{id}/reveal` `{ purpose: "Reveal" | "Copy" }` → `{ secret }` (gateway-admin only; writes an audit entry `reveal`/`copy` with the actor; 409 for revoked keys and for keys created before the secret was stored, `VirtualKey.canReveal` is false for those)
- `POST /api/keys/{id}/revoke` → `VirtualKey` (immediate, also cancels grace)
- Rotation retains the predecessor's key budgets and spend across the full rotation lineage, including
  the grace-period key. It does not reset the remaining budget. Concurrent conflicting mutations → 409.
  The replacement gets copies of the key's MCP grants (the audit entry's `after` lists them as `mcpGrants`).
- MCP grants: see [MCP servers](#mcp-servers-and-grants).

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
- `PUT /api/providers/{id}` same body; `credential`: omitted/null = unchanged, `""` = remove
- `POST /api/providers/{id}/drain` `{ drained: boolean }` → `Provider` · `DELETE /api/providers/{id}` (409 if models exist)
- `POST /api/providers/{id}/discover-models` → `DiscoveredModel[] = { id, displayName, kind, parameterProfile, contextWindow|null, features: string[], price: { inputPerMillionUsd, cachedInputPerMillionUsd, outputPerMillionUsd }|null, alreadyAdded }`. Calls the provider's `/models` with the stored credential, so it doubles as a connection test (502/504 on failure). Context size, features and price are included only when the provider reports them. Azure OpenAI is unsupported (400). In Production the provider address must be https (400 otherwise), like the gateway; one attempt, 20 s timeout.
- `Price = { inputPerMillionUsd, cachedInputPerMillionUsd, outputPerMillionUsd, audioPerMinuteUsd, audioInputPerMillionUsd, audioOutputPerMillionUsd, effectiveFrom }`
  (`audioPerMinuteUsd` is for models billed by duration, such as Whisper and gpt-realtime-translate; `audioInputPerMillionUsd`
  and `audioOutputPerMillionUsd` price the audio part of the tokens of realtime and gpt-4o-transcribe models, 0 = billed
  as text tokens; all three omitted on create = 0)
- `ProviderCapabilities`: `ChatCompletions`, `Embeddings`, `Responses`, `AnthropicMessages`, `Streaming`, `AudioTranscriptions`
  (speech to text), `Realtime` (live audio over WebSocket). `ModelKind` (models and routes): `Chat`, `Embedding`,
  `Transcription`, `Realtime`, `SpeechTranslation`. Discovery adds Whisper and `*-transcribe` models as `Transcription`,
  `*realtime-translate*` as `SpeechTranslation` and other `*realtime*` models as `Realtime`; speech synthesis, image and
  chat-with-audio models are still left out.
- `baseUrl` may carry query parameters (e.g. `?api-version=…` for Foundry Voice Live); they are kept after the path the
  gateway appends. Parameters that look like credentials (`*key*`, `*token*`, `*secret*`, `*auth*`, `sig`, `code`) are refused.
- `Model = { id, providerId, providerName, residency, name, upstreamModel, kind, parameterProfile, contextWindow, isEnabled, features: string[], currentPrice: Price|null }`
- `GET /api/models` · `POST /api/models` `{ providerId, name, upstreamModel, kind, parameterProfile, contextWindow?, isEnabled, features?: string[], price?: Price }`
- `PUT /api/models/{id}` `{ name, upstreamModel, kind, parameterProfile, contextWindow?, isEnabled, features?: string[] }` · `DELETE /api/models/{id}` (409 if used by a route)
- `GET /api/models/{id}/prices` → `Price[]` · `POST /api/models/{id}/prices` `Price` (effectiveFrom optional = now)
- `Route = { id, name, description, kind, isEnabled, targets: [{ modelId, modelName, providerName, residency, priority, weight }] }`
- `GET /api/routes` · `POST /api/routes` · `PUT /api/routes/{id}` body `{ name, description?, kind, isEnabled, targets: [{ modelId, priority, weight }] }` · `DELETE /api/routes/{id}`

## MCP servers and grants
Concepts and the gateway side: [mcp.md](mcp.md). Every endpoint needs `gateway-admin` or `department-admin`. A gateway admin
sees and manages every server; a department admin sees global servers (read-only) and the servers owned by their
departments (manageable), and gets 404 for other departments' servers and 403 for changes to global ones. Every change
is audited (entity types `McpServer`, `McpTool`, `McpGrants`) and makes the gateways reload their catalogue.

- `McpServer = { id, slug, displayName, description, url, residency, isEnabled, ownerDepartmentId|null, ownerDepartmentName, authType: McpAuthType,
  hasCredential, forwardHeaders: string[], timeoutSeconds, maxResultBytes, autoApproveNewTools, status: McpServerStatus, lastCheckedAt, lastError,
  capabilities: McpServerCapabilities[], instructions, createdAt, toolCount, pendingToolCount, changedToolCount, canManage }`
  (the credential is **write-only**; `status`, `lastCheckedAt`, `lastError`, `capabilities` and `instructions` come from the last sync)
- `GET /api/mcp/servers` · `GET /api/mcp/servers/{id}`
- `POST /api/mcp/servers` · `PUT /api/mcp/servers/{id}` `{ slug, displayName, url, residency, authType?, description?, isEnabled?, ownerDepartmentId?,
  forwardHeaders?, timeoutSeconds? (1–900, 30), maxResultBytes? (1 KiB–16 MiB, 1 MiB), autoApproveNewTools?, credential? }` → `McpServer` (201 on create)
  - `slug`: lower-case letters and digits, starting with a letter, single `_` between them, at most 24 characters; unique (409).
    Changing it renames the server's tools on `/mcp`.
  - `url`: the providers' rule (absolute `https`, `http` only for `OnPrem`, no user info, fragment or credential-like query
    parameters); `https` only in Production. SSRF: the host is resolved when the URL is checked; link-local
    (169.254.0.0/16, fe80::/10), cloud metadata addresses (also 168.63.129.16, 100.100.100.200, fd00:ec2::254), unspecified,
    multicast and reserved addresses are always refused, and private ones (loopback, RFC 1918, 100.64.0.0/10, fc00::/7) only for
    `gateway-admin` (a department admin may keep a private URL a gateway admin set). A name that does not resolve is refused.
    Errors are field errors on `url` (or `credential` for the token URL).
  - `forwardHeaders`: caller headers sent on, by exact name (at most 20, unique). `Authorization`, `x-api-key`, `api-key`,
    cookies and HTTP/MCP protocol headers are refused.
  - `credential`: `{ headers: { name: value } }` for `StaticHeaders` (1–20 headers; HTTP/MCP protocol headers such as `Host` or
    `Mcp-Session-Id` refused) or `{ tokenUrl, clientId, clientSecret, scope? }` for `OAuthClientCredentials` (the token URL follows
    the URL rules). Omitted = keep the stored one (only for the same `authType`; a new auth type needs a credential → 400);
    `authType: "None"` removes it. Stored encrypted like provider credentials; never returned or audited.
  - A department admin must set `ownerDepartmentId` to one of their departments (403 without, 400 for another department).
- `DELETE /api/mcp/servers/{id}` → 204 (its tools and every key's grant on it go too)
- `POST /api/mcp/servers/test` (body as for create) and `POST /api/mcp/servers/{id}/test` (optional body: the stored server with
  these changes; omitted credential = the stored one) → `{ succeeded, error|null, httpStatus|null, capabilities, instructions, tools: [{ name,
  title, description, exposedName }], warnings: string[] }`. Connects with the gateway's MCP client and lists the tools; nothing is saved.
- `POST /api/mcp/servers/{id}/sync` → `{ status, lastCheckedAt, lastError, added, changed, reverted, updated, removed, errors }` (tool
  names). Reads the server's tools now (see *Tool sync* in [mcp.md](mcp.md)); 409 while another sync runs. Always audited (`sync`).
- `McpTool = { id, serverId, name, title, exposedName, approvalState, isExposed, definition, definitionHash, approvedDefinition|null, priceSek|null,
  discoveredAt, approvedAt, approvedBy, changes: [{ path, kind: "Added"|"Removed"|"Changed", before, after }] }`. `definition` is what the
  server offers now (JSON: `title`, `description`, `inputSchema`, `outputSchema`, `annotations`), `approvedDefinition` what clients see.
  `changes` (only for `Changed`) lists the differences from the approved definition to the current one; `path` is a JSON Pointer
  (`/inputSchema/properties/q/type`), objects are compared member by member and arrays item by item.
- `GET /api/mcp/servers/{id}/tools` → `McpTool[]` (by name; department admins can read global servers' tools)
- `POST /api/mcp/servers/{id}/tools/{toolId}/approve` `{ definitionHash }` → `McpTool`. Approves the current definition (sets
  `approvedDefinition`, `approvedAt`, `approvedBy`, the title and the exposed name); **409** when `definitionHash` is not the
  current one (the server changed it since it was reviewed) or another tool has the exposed name. The audit entry holds the
  approved definition.
- `POST /api/mcp/servers/{id}/tools/{toolId}/reject` → `McpTool`: hidden from every key until approved again (an earlier approval is dropped).
- `PUT /api/mcp/servers/{id}/tools/{toolId}/price` `{ priceSek: number|null }` (0–1 000 000 SEK per call, null = free) → `McpTool`
- A sync and an approval of the same tool at the same time: the later save gets 409 (concurrency check).
- `McpGrant = { serverId, serverSlug, serverName, allowedTools: string[], approvalPolicy: McpApprovalPolicy, allowResources, allowPrompts, unknownTools: string[] }`
- `GET /api/keys/{id}/mcp-grants` → `McpGrant[]` (only grants on servers the user can see)
- `PUT /api/keys/{id}/mcp-grants` `[{ serverId, allowedTools, approvalPolicy? (Always), allowResources? (false), allowPrompts? (false) }]` → `McpGrant[]`.
  Replaces the key's grants on the servers the user can see (grants on other servers stay). `allowedTools` are the server's
  tool names (case-sensitive, 1–128 characters, at most 1 000) or `["*"]` for all (`*` among names stores `["*"]`). Names the
  server does not offer are kept and returned in `unknownTools`: they allow nothing until the server offers such a tool (so
  grants can be prepared before a sync). 400 for a server listed twice or one the user cannot see, at most 500 servers.
- Settings: `Admin:McpSyncMinutes` (10, 0–1440; 0 = only on demand).

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
- `Budget = { id, scope, scopeId, scopeName, limitSek, period, alertThresholds: number[], isActive, periodStart, periodEnd, spentSek, percentUsed }`
- `GET /api/budgets?scope=&scopeId=` · `POST /api/budgets` `{ scope, scopeId, limitSek, period, alertThresholds, isActive }` · `PUT /api/budgets/{id}` · `DELETE /api/budgets/{id}`
- `GET /api/alerts?acknowledged=false` → `[{ id, budgetId, scope, scopeName, thresholdPercent, spentSek, limitSek, periodStart, timestamp, acknowledged }]`
- `POST /api/alerts/{id}/acknowledge` → 204
- `GET /api/settings/exchange-rate` → `{ currency: "USD", sekPerUnit, effectiveFrom }`, or 200 with an empty body when no rate has been entered · `PUT /api/settings/exchange-rate` `{ sekPerUnit }`.
  Until a rate is in effect the gateway prices with `Gateway:FallbackSekPerUsd` (default 10, 0.01–1000), logs a warning and
  reports its `exchange-rate` readiness check (and so the gateway component in `/api/ops/health`) as Degraded.
- Budget alerts are stored with the usage batch that crossed the threshold. With `Gateway:AlertWebhookUrl` set, the gateway
  also POSTs each alert once (metadata only, 5 s timeout, no retries) from its own queue of 1 000; when that queue is
  full the alert is stored but not sent (a warning is logged).

## Usage (metadata only – never prompt/response content)
- `GET /api/usage/summary?from=&to=&groupBy=department|team|key|model|provider|day&departmentId=&teamId=`
  → `{ from, to, totalCostSek, totalRequests, totalInputTokens, totalOutputTokens, totalErrors, rows: [{ key, label, requests, inputTokens, outputTokens, costSek, errors, fallbacks }] }`
- `GET /api/usage/requests?from=&to=&keyId=&teamId=&departmentId=&outcome=&traffic=&page=1&pageSize=50`
  → `{ items: UsageRequest[], total }` where `UsageRequest = { requestId, timestamp, keyPrefix, keyName, teamName, departmentName, endpoint, requestedModel, providerName, upstreamModel, inputTokens, cachedInputTokens, outputTokens, costSek, latencyMs, statusCode, outcome, fallbackCount, streamed, piiActionApplied, piiCategories, errorCode, routingRuleId, routingRuleName }` (`routingRuleName`: the routing rule that decided where the request went, `null` when none applied)
- `traffic` (both lists, case-insensitive): `mcp` = MCP calls (endpoints `/mcp` and `/v1/mcp/...`), `llm` = everything else; anything else → 400.
- `GET /api/usage/requests/{requestId}` → `UsageRequest` (lookup by `x-request-id` from gateway response)
- `GET /api/usage/pii-incidents?from=&to=&keyId=&teamId=&departmentId=&category=&action=&traffic=&page=1&pageSize=50` (any role, own departments)
  → `{ from, to, items: PiiIncident[], total, totalIncidents, categories: [{ category, requests, occurrences }], actions: [{ action, requests }] }`
  where `PiiIncident = { requestId, timestamp, keyId, keyPrefix, keyName, teamId, teamName, departmentId, departmentName, endpoint, requestedModel, providerName, action, categories: [{ category, count }], outcome, statusCode, routingRuleName }`.
  An incident is a usage record with PII found (`piiCategories` set), newest first; this includes policy `Allow` (detected and sent on).
  `category` is `Personnummer|Samordningsnummer|Email|Phone|Iban`, `action` is `Allow|Redact|Block|RerouteToOnPrem` (case-insensitive;
  anything else is 400). `items`/`total` honour every filter; `categories`, `actions` and `totalIncidents` cover the period and the
  department/team/key filters only. Default period: the last 30 days, at most one year. Keys with PII policy `Off` are not scanned and have no incidents.
- `GET /api/usage/export.csv?from=&to=` → CSV: ansvarskod, förvaltning, team, nyckel, requests, tokens, kostnad SEK.
  Semicolon-separated; cells are quoted only when they contain a semicolon, a quote or a line break. A text cell a spreadsheet would run as
  a formula (starting with a tab or line break, or with `=`, `+`, `-` or `@` after any leading spaces) is prefixed with `'`;
  numbers such as `-1.5` are left as they are. The Data API's CSV (comma-separated) uses the same rule.

## Catalogue (developer portal, any authenticated user)
- `GET /api/catalog` → `{ gatewayBaseUrl, routes: [{ name, description, kind, residencies: DataResidency[], capabilities: ProviderCapabilities[], inputSekPerMillion, outputSekPerMillion }], models: [{ name, kind, residency, capabilities, inputSekPerMillion, outputSekPerMillion }] }`

## Operations (gateway-admin)
- `GET /api/ops/health` → `{ checkedAt, components: [{ name, status: "Healthy"|"Degraded"|"Unhealthy", description }], versions: { adminApi, gateway: string|null, schema }, usageWriter: { queueDepth, capacity, inFlightRecords, lastWriteAt: string|null, consecutiveFailures }|null, providers: [{ id, name, type, residency, isEnabled, isDrained, circuitState, requests24h, errorRate24h, providerErrorRate24h, fallbackRate24h, p50LatencyMs, p95LatencyMs, status, statusReason, recentRequests, recentProviderErrors }], mcpServers: [{ id, slug, displayName, isEnabled, status, lastCheckedAt, lastError, circuitState, pendingTools, changedTools }] }` (providers sorted by name, MCP servers by slug; the latency percentiles leave out live audio sessions, whose `latencyMs` is their length; an MCP server's `status` is from its last sync)
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
- `POST /api/ops/mcp-servers/{id}/circuit` `{ state: "Open" | "Closed" }` → 204 (force open = hide the server's tools for 15 min; audited as `circuit` on `McpServer`)
- `POST /api/ops/cache/invalidate-keys` → 204 (audited as `invalidate` on `Keys`; the cache invalidations that
  ordinary changes publish are not audited separately)
- `GET /api/ops/config/export` → JSON document (providers and MCP servers without credentials, models, prices, routes, global routing rules, MCP tool approvals)
- `POST /api/ops/config/import` (same JSON) → `{ created, updated, skipped }`
- Config schema: `{ schemaVersion: 1, providers: [{ configuration: ProviderRequest (without credential),
  credentialEnvironment?: "UME_PROVIDER_..." }], models: [{ providerName, configuration: ModelRequest,
  prices: Price[] }], routes: [{ name, description, kind, isEnabled,
  targets: [{ modelName, priority, weight }] }], routingRules?: [{ name, description?, isEnabled, priority, condition?, chain,
  targets: [{ model, weight }], fallbacks?: string[] }], mcpServers?: [{ configuration: McpServerRequest (without credential and
  ownerDepartmentId), ownerDepartmentCode?, credentialEnvironment?: "UME_MCP_...", tools?: [{ name, approvalState: "Approved"|"Rejected",
  approvedDefinition?, priceSek? }] }] }`. Only **global** routing rules are transferred (key/team/department ids differ between
  environments), and not untouched templates (every installation has them); rules are matched by name, created or updated, never deleted by import, and each is validated like one saved through the API (an invalid rule fails the whole import).
  Documents without `routingRules` (older exports) still import.
  MCP servers are matched by slug and their owner by the department's cost centre code (unknown → 400); keys' MCP grants are
  not transferred (key ids differ). Exported tools are the approved ones (with the definition clients see; `Changed` tools as
  `Approved` with the approved definition) and the rejected ones. On import an approved tool is approved (`Changed` if the
  server here currently offers another definition); one this environment has not discovered yet is created approved, and the
  next sync compares it with what the server offers. A rejected tool that does not exist here is skipped (`skipped`). Pending
  tools are not transferred; the next sync finds them. The credential comes from `credentialEnvironment` (the credential JSON,
  as in `credential` above but as the gateway stores it: a header object, or `{tokenUrl, clientId, clientSecret, scope}`) or stays as it is. Changing the kind of a model that a route uses → 409
  "Modelltypen kan inte ändras när modellen används i en rutt." (same as `PUT /api/models/{id}`).
  References use stable names; database IDs in model configuration are ignored on import.
  Import is transactional, rejects plaintext credentials, preserves existing encrypted credentials when
  a reference is omitted, and never overwrites price history. Environment references must already exist
  in the Admin API process. Export never contains secret values or encrypted credentials.
- `GET /api/audit?page=&pageSize=&entityType=` → `{ items: [{ id, timestamp, actor, action, entityType, entityId, details }], total }`
  - `details` is JSON `{ before, after }` (camelCase, enums as names) with snapshots of the entity's own stored settings,
    never a secret: no derived or volatile values such as `deploymentCount`, `createdAt`, `currentPrice`, `providerName`,
    key `status` or `lastUsedAt`. For a rotation `after` is `{ previousKey, key }`, for a reveal/copy the key's name and
    prefix, for a config import the counts `{ created, updated, skipped }`, for an MCP sync the tool names it changed
    (`{ status, lastError, capabilities, added, changed, reverted, updated, removed }`), for a tool approval the approved definition.

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
- Live audio: see [below](#live-audio-realtime).
- MCP: `POST /mcp` and `POST /mcp/{slug}` (JSON-RPC; see [mcp.md](mcp.md)). HTTP-level errors use the error body
  below (`invalid_api_key` with `WWW-Authenticate: Bearer`, `mcp_server_not_found` for an unknown or not granted slug);
  everything after that is a JSON-RPC error. Usage records of refused or failed tool calls carry `mcp_tool_not_found`,
  `mcp_invalid_arguments`, `mcp_upstream_failed` or `mcp_result_too_large`.
- MCP tools for a client's own tool loop: `GET /v1/mcp/tools?format=openai|responses|anthropic` → `{ object: "list", format,
  data: [tool definition] }` and `POST /v1/mcp/tools/call` (one `tool_calls` entry, Responses `function_call` or Anthropic
  `tool_use` → the `tool` message, `function_call_output` or `tool_result` to append; optional request header
  `x-ume-parent-request-id`). Errors use the error body below, with `mcp_tool_not_found` (404), `mcp_invalid_arguments` (400),
  `mcp_upstream_failed` (502/504) and `mcp_result_too_large` (502) besides the usual codes; see
  [mcp.md](mcp.md#using-mcp-tools-with-the-llm-apis). Usage records have `endpoint` `McpToolCall`.
- Hosted MCP tools on `POST /v1/responses` (`{"type":"mcp","server_label":"<slug>",…}`, non-streaming): the gateway runs the
  tool loop and answers with `mcp_list_tools`, `mcp_call` and `mcp_approval_request` items, a summed `usage` and, when calls
  are still to be answered, a `resp_ume_…` id to continue from; see [mcp.md](mcp.md#hosted-mcp-tools-on-v1responses). Extra
  header `x-ume-turns`; extra error codes `mcp_server_not_found` (404), `mcp_continuation_not_found` (404/409),
  `mcp_approval_not_found` (404/409), `mcp_approval_mismatch` (400). Later turns and tool calls are usage records with
  `ParentRequestId` set to the first turn's request id.
- Response headers: `x-request-id`, `x-ume-provider`, `x-ume-model`, `x-ume-fallbacks`, `x-ume-cost-sek`,
  `x-ratelimit-limit-requests`, `x-ratelimit-remaining-requests`, `x-ume-budget-remaining-sek`,
  `x-ume-rule` (id(s) of the routing rule(s) that applied; see [routing rules](routing-rules.md)).
- Error body (OpenAI style): `{ "error": { "message", "type", "code", "request_id", "doc_url" } }` with codes
  `invalid_api_key`, `key_expired`, `key_revoked`, `key_disabled`, `model_not_allowed`, `model_not_found`, `model_ambiguous`, `budget_exceeded`,
  `rate_limited`, `pii_blocked`, `attachment_not_allowed`, `no_eligible_provider`, `all_providers_failed`, `invalid_request`,
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
  breaker. Other client errors (e.g. 400) are returned as they are.
- `GET /v1/models` lists only aliases and models the key may use and that some endpoint of their kind can reach, by the
  same rule as routing: model and provider enabled and not drained, a provider capability for that endpoint, and the
  key's provider and residency allow-lists.
- Outgoing requests to providers carry `User-Agent: Ume-LlmGateway/<version>`.
  A provider-side refusal is passed through with the provider's status; one the gateway raises itself is
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
  provider and residency allow-lists), `pii_blocked`, `invalid_request` (binary frame),
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

**Key budgets.** A key can have one budget per period (`Hourly`, `Daily`, `Weekly`, `Monthly`, `Quarterly`, `Yearly`) via `POST /api/budgets` with `scope: "VirtualKey"`; a second budget for the same owner and period returns 409. The gateway enforces every applicable budget (key, team, department) and rejects with 402 when any is exhausted. Hourly windows are aligned to the clock hour. `requestsPerMinute` and `tokensPerMinute` on the key are enforced per minute (429 `rate_limited`; token usage is counted after each response).
