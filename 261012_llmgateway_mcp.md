# MCP support: analysis and implementation plan

2026-10-12. Branch `mcp` (currently identical to `main` at `6a40bf0`).

This document looks at how Bifrost (`../bifrost`, docs at https://docs.getbifrost.ai/mcp/overview) implements the Model Context Protocol. It then proposes how the Ume LLM Gateway should add MCP support. The goal is to keep what Bifrost gets right, avoid its known weak spots, and fit our existing architecture. That architecture means the governance hierarchy, the catalogue snapshot, Redis accounting, the PII guard, residency and the "no Postgres on the hot path" rule. `docs/architecture.md` currently lists "an MCP gateway" as out of scope, so this plan also needs ADR 016.

---

## Part 1: How Bifrost does it

### Architecture

- MCP lives in Bifrost's Go core (`core/mcp/*`). The protocol work (transports, JSON-RPC, server) is delegated to `mark3labs/mcp-go`.
- An `MCPManager` holds:
  - a map of `MCPClientState` (one per upstream server);
  - a `ToolsManager`;
  - an in-process MCP server for tools registered from code;
  - a health checker per client;
  - a credential store;
  - a plugin pipeline.
- Core has no DB access. It reports tool and state changes through callbacks, and the HTTP transport persists them in `config_mcp_clients`. Discovered tools are persisted too, so a restart keeps the last-known tool set.
- Transports:
  - `stdio` (a subprocess);
  - `http` (Streamable HTTP);
  - `sse` (legacy);
  - `inprocess` (SDK only).
- Connection mode is a second, separate axis:
  - sticky: one shared connection;
  - per-call: `initialize`, then call, then close, on every tool call. This is the default for HTTP.
- Health:
  - It pings every 10 min while healthy and every 10 s while unstable.
  - Reconnects are make-before-break for HTTP and SSE.
  - On a 401/403 it refreshes credentials and retries once inline. It does not retry tools annotated as destructive or non-idempotent.
  - The `needs_reauth` state is sticky and needs an admin to clear it.

### Tool discovery and injection

- Tools are renamed `<client>-<tool>`. Client names cannot contain `-`, so the first hyphen splits the name back reliably.
- **By default, every permitted MCP tool is appended to every chat and responses request**, on every provider attempt. `disable_auto_tool_inject` turns this into opt-in through headers.
- Upstream `instructions` are wrapped in `<mcp_server>` blocks, size-capped, and prepended as a system message.
- Filtering has three layers, and each can only narrow:
  1. Per-client `tools_to_execute`. Deny by default; `["*"]` allows everything.
  2. Request headers `x-bf-mcp-include-clients` / `x-bf-mcp-include-tools`.
  3. The virtual key's `mcp_configs`. A governance plugin enforces this layer by rewriting a context key.
- Execution re-checks the same filters, so a hidden tool cannot be called by name. "Not permitted" is generic, so the tool's existence doesn't leak.
- Duplicates with caller-supplied tools are found by **sniffing the User-Agent** and parsing each CLI's own naming scheme (Claude Code, Codex, Gemini, ...).

### Execution

- **Explicit:** `POST /v1/mcp/tool/execute` takes a `tool_call` and returns a `role:"tool"` message to append to the history.
- **Agent mode:** this is implicit and configured per client in `tools_to_auto_execute`. When a response contains tool calls:
  - auto tools run in parallel;
  - the conversation is rebuilt and resent through the full plugin pipeline;
  - this repeats up to `max_agent_depth` (default 10).
- With a mix of auto and manual tools, the executed results are pasted into assistant `content` as JSON keyed by tool name, followed by "Now I shall call these tools next...". `finish_reason` is forced to `stop` and the intermediate turns are dropped.
- **There is no agent mode for streaming requests.**

### Code Mode

- A client marked `is_code_mode_client` is replaced by four meta-tools: `listToolFiles`, `readToolFile`, `getToolDocs` and `executeToolCode`.
- The model writes a script that calls `server.tool(...)`. The script runs in an in-process **Starlark** interpreter (marketed as Python), with caps on steps, memory and size, and one 30 s timeout for the whole script.
- Every nested call is authorised and goes through the plugin pipeline.
- The point is saving tokens. Bifrost claims -58 % input tokens at 96 tools and -93 % at 508 tools.

### Bifrost as an MCP server

- `POST /mcp` and `/mcp/{slug}` expose the aggregated tools, filtered per caller, to Claude Desktop, Cursor and similar clients.
- Inbound auth is the virtual key in a header, or Bifrost acting as a full OAuth 2.1 authorization server (DCR, PKCE, RFC 9728 and RFC 8707, a consent page).
- "Virtual MCPs" bundle tools under a slug.
- Auto-execute settings are ignored on this path; the host app does the approval.

### Governance and security

- Upstream auth types:
  - `none`, `headers`, `oauth`;
  - `per_user_oauth`, `per_user_headers`. Credentials are stored per user, virtual key or session, and a missing credential returns a typed `mcp_auth_required` error with an authorize URL;
  - `token_exchange` (RFC 8693).
- Caller headers are forwarded only if they are on a per-client allowlist.
- Plugins get pre and post hooks for both execution and connection, and never see `Authorization`.
- Per-tool pricing is charged against virtual-key budgets.
- `MCPToolLog` records every call with correlation IDs, and there are OTel spans.
- SSRF guards block private, loopback and metadata targets unless dashboard auth is on.

### What Bifrost gets right (copy these)

1. Discovery and execution use the same filter, so a hidden tool cannot be called.
2. Filters can only narrow, from server to key to request.
3. A typed `mcp_auth_required` error.
4. Persisted tool snapshots, written only when their hash changes.
5. Make-before-break reconnects. Destructive tools are never retried.
6. Connect-time hooks never see credentials.
7. Per-tool cost is charged to budgets.
8. Code Mode authorises at each actual call, not by static analysis.

### Where Bifrost falls short (do better)

| # | Weakness | Consequence |
|---|---|---|
| 1 | Results are flattened to one string. Images and audio become base64 *text*, and `structuredContent` and embedded resources are dropped. The `/mcp` proxy re-wraps the string as a single text block. | A lossy proxy that wastes tokens and gives the model no usable image. |
| 2 | Tools only. There are no resources, prompts, progress, cancellation or `list_changed`. `GET /mcp` is a stub and there is no `Mcp-Session-Id` handling. | Not a real MCP proxy. Clients never learn that tools changed. |
| 3 | The agent-mode response format is invented and lossy. Results are pasted into content, a fake `stop` is returned, and intermediate turns are dropped. Hitting max depth silently returns unexecuted calls. | Callers cannot rebuild a faithful history or tell what happened. |
| 4 | No streaming agent mode. It cannot be switched on or off per request. | It doesn't fit chat UIs, which all stream. |
| 5 | Auto-execute policy is per server, not per key or request. | Two tenants cannot have different approval policies for the same server. |
| 6 | Everything is injected into every request by default. Instructions change the system prompt. | Token cost and broken prompt caching. Bifrost's own docs recommend turning it off. |
| 7 | Deduplication by sniffing the User-Agent. | Brittle and vendor-specific. |
| 8 | No 64-character cap on tool names, and permissive characters. | Provider rejects requests at runtime. |
| 9 | Per-call HTTP is the default. | An extra `initialize` round trip on every call, and server session state is lost. |
| 10 | Stdio runs inside the gateway process. The subprocess likely inherits the gateway's environment, including provider keys. | Secret exposure, and arbitrary command execution from the admin UI. |
| 11 | Key-level filtering only holds when the governance plugin is loaded. Without it, request headers can widen access. Session identity can be asserted by the client. | Security depends on configuration. |
| 12 | Seven connection states, six auth types, two connection axes, deprecated aliases, and a bare number read as nanoseconds. `clientmanager.go` is 3.5k lines. | Hard to operate and reason about. |
| 13 | Tool descriptions are trusted as-is, and changes are applied silently at the next sync. | "Rug-pull" and tool-poisoning prompt injection. |
| 14 | Upstream residency and data classification are not modelled. | Cannot tell a key "only on-prem tools". |

---

## Part 2: How the Ume LLM Gateway should do it

### Guiding principles

1. **Proxy first, agent second.** The most valuable and least risky feature is a governed, audited **MCP gateway**: one `/mcp` endpoint where municipal MCP servers are exposed to clients under virtual keys, budgets, PII policy and residency. Server-side tool execution inside LLM requests comes later and is opt-in.
2. **Use standard wire formats, not invented ones.** For gateway-executed tools, implement the shapes the providers already define:
   - the OpenAI Responses hosted `mcp` tool: `mcp_list_tools`, `mcp_call`, `mcp_approval_request` / `mcp_approval_response`, `max_tool_calls`, and the `response.mcp_call.*` stream events;
   - the Anthropic Messages MCP connector: `mcp_servers` / `mcp_toolset`, `mcp_tool_use` / `mcp_tool_result`.
   
   Existing SDKs then work unchanged, history stays faithful, and approval is per request. This fixes Bifrost weaknesses 3, 4, 5 and 7.
3. **Full fidelity.** MCP content blocks (text, image, audio, resource links, embedded resources, `structuredContent`) pass through the proxy unchanged. On the LLM path they are converted to the provider's native multimodal parts, not to base64 text (weakness 1).
4. **Nothing is injected implicitly.** MCP tools reach a model only when the request asks for them (weakness 6). The LLM hot path for requests without MCP is unchanged; there is no extra cost on `/v1/chat/completions`.
5. **Governance lives in the pipeline, not a plugin.** Key grants are part of `CatalogSnapshot` and checked in the same stage for `tools/list` and `tools/call`. They cannot be bypassed by configuration (weakness 11).
6. **No subprocesses in the gateway.** We support remote Streamable HTTP only. Stdio servers run as their own hardened container behind a bridge on an internal network (weakness 10).
7. **Pin tool definitions.** The admin approves the tool set. Changed descriptions or schemas are quarantined until re-approved (weakness 13).
8. **Small state model.** Three server states, three upstream auth types at launch, one connection mode (pooled and sticky with automatic re-initialize).

### Decisions

- **SDK:** the official C# SDK (`ModelContextProtocol.AspNetCore`, which brings `ModelContextProtocol` and `ModelContextProtocol.Core`; maintained by Microsoft and Anthropic), pinned at **2.2.0** (2026-08-13) in `Directory.Packages.props`. Target protocol revision **`2026-07-28`** (the current one, see *Protocol revision* below), with `2025-11-25` and `2025-06-18` negotiated for clients that still use the `initialize` handshake. We use the SDK for JSON-RPC framing, the Streamable HTTP client to upstreams, and the server transport. Our own code owns authorisation, the aggregation, naming and accounting. Tools, resources and prompts the gateway only knows as data are served with low-level handlers (`WithListToolsHandler`, `WithCallToolHandler`, ...), never registered as C# methods.
- **Protocol revision (verified 2026-10-12, corrects the first draft of this plan):** `2026-07-28` made MCP stateless. It removes the `initialize` handshake (version and client capabilities travel in every request's `_meta`), protocol-level sessions and `Mcp-Session-Id`, the HTTP `GET` stream, `ping`, `logging/setLevel` and SSE resumability. It adds the mandatory `server/discover`, `subscriptions/listen` (one long-lived POST response stream for opted-in `*/list_changed` and resource notifications), a required `resultType` on results, `ttlMs`/`cacheScope` on list results, and multi round-trip requests (`input_required`) in place of server-initiated sampling, elicitation and roots requests. Sampling, roots and logging are deprecated. The SDK serves both eras from one endpoint: its default `HttpServerSessionMode.Stateless` answers handshake clients too (tested with `2025-11-25` and `2025-06-18` clients); the gateway runs it in `HttpServerSessionMode.Stateless` for all clients, with no session state of its own (below). `StatefulForInitializeClients` would keep in-memory sessions for handshake clients, which needs instance affinity.
- **Spike results (`tests/Ume.LlmGateway.Gateway.Tests/McpSdkTests.cs`):**
  - `HttpClientTransport` (Streamable HTTP) plus `McpClient.CreateAsync`, `ListToolsAsync` and `CallToolAsync` work against an in-process server on ASP.NET `TestServer`.
  - A server built only from `WithListToolsHandler` / `WithCallToolHandler` proxies renamed tools; text, image and `structuredContent` round-trip unchanged through the typed models.
  - Limitation: the typed `CallToolResult`/`ContentBlock` models throw on a content block type they do not know, and a handler must return the typed result. For pure proxying, an incoming message filter (`WithMessageFilters(f => f.AddIncomingFilter(...))`) can answer `tools/call` itself with `Server.SendMessageAsync(new JsonRpcResponse { Result = <raw JsonNode> })`, which passes unknown blocks and vendor fields through. On the client side, `McpSession.SendRequestAsync(JsonRpcRequest)` returns the raw result. The proxy should therefore forward raw JSON for `tools/call`, `resources/read` and `prompts/get`, and use the typed models only for what the gateway itself builds (lists, errors).
  - The SDK server adds `_meta["io.modelcontextprotocol/serverInfo"]` (the gateway's identity) to every result, as `2026-07-28` recommends; upstream `_meta` is otherwise kept.
  - Images use base64 UTF-8 bytes internally: `ImageContentBlock.FromBytes(bytes, mime)` and `DecodedData`, not `Data`, hold the decoded image.
- **Where it runs:** in the `Gateway` project (the data plane), on a new set of endpoints. There is no new service. The realtime relay (`GatewayRequestHandler.Realtime.cs`, `IRealtimeSessionRegistry`) is the template for a long-lived, metered, per-key session.
- **Upstream transport:** Streamable HTTP only. Legacy SSE is not supported; a server that only speaks SSE goes behind the stdio bridge. Stdio servers run as separate containers (for example `ghcr.io/sparfenyuk/mcp-proxy`, or a tiny .NET host built on the SDK). Like everything else they use chiseled, nonroot images with read-only rootfs, sit on a new `mcp` Docker network, and get only their own secrets. ADR 008 hardening applies to them.
- **Connection mode:** one pooled upstream client per (server, credential). Against a `2026-07-28` upstream there is no session to keep, so this is just a shared, kept-alive HTTP client. Against a handshake-era upstream the pooled session is kept alive and re-initialised transparently when the upstream returns 404 for `Mcp-Session-Id` or the session drops. Bifrost's per-call default costs an extra round trip and loses server session state (weakness 9).
- **Downstream sessions:** none. The gateway's MCP server runs the SDK in `HttpServerSessionMode.Stateless` for every client, including handshake-era ones (`2025-11-25`, `2025-06-18`): every request is authenticated by its virtual key and any gateway instance serves any request. The gateway issues no `Mcp-Session-Id` and keeps no session state, and there is no `GET` notification stream. Change notifications (for example `tools/list_changed` after an approved tool change) are delivered only on `subscriptions/listen` streams (`2026-07-28`), served by the instance that holds the stream and fed by Redis pub/sub on `ume:mcp:events`; handshake-era clients see changes when they reconnect and list tools again. The channel is in `RedisKeys` and `deploy/redis/users.acl.example`.
- **Inbound auth:** virtual keys only, as on `/v1` (Bearer, `x-api-key`, `api-key`). Claude Desktop, Claude Code, VS Code and Cursor all support static headers for remote MCP servers. Acting as an OAuth 2.1 authorization server is deferred (open point 1).
- **Upstream auth at launch:**
  - `None`;
  - `StaticHeaders`: an encrypted credential through `CredentialProtector`, like `ProviderAccount`;
  - `OAuthClientCredentials`: a service-to-service token, refreshed and cached per instance.

  Per-user OAuth and token exchange need end-user identity, which the gateway does not have today (keys only, no SSO). They are deferred and designed for later. The caller's virtual key is **never** forwarded upstream. Caller headers are forwarded only if the server's `ForwardHeaders` allowlist names them exactly.
- **Accounting:** each MCP JSON-RPC request that reaches an upstream (`tools/call`, `resources/read`, `prompts/get`) is one `UsageRecord`, with `Endpoint = Mcp`. The record has new nullable columns `McpServerId`, `McpMethod` and `McpTool`, plus `ParentRequestId` for calls the gateway makes inside an LLM request. This keeps the invariant "every accepted request is accounted exactly once", and DataApi and BI see MCP traffic without a new table. Cost is an optional per-server or per-tool price in SEK (`McpToolPrice`), reserved and reconciled through `BudgetService` like tokens. Discovery calls (`initialize`, `tools/list`) are served from the catalogue, never sent upstream, and not recorded.
- **Rate limiting:** a new `McpCallsPerMinute` on `VirtualKey` (null means no limit), enforced in Redis next to the RPM limit. There is also a per-key cap on concurrent `subscriptions/listen` streams, like the realtime session cap.
- **PII:** the key's `PiiPolicy` applies to `tools/call` arguments, through `PiiJsonScanner` (the tool `name` is skipped as structural). New: **tool results are also scanned**, because on the LLM path they become model input. `Redact` redacts results before they reach the model or the client. `RerouteToOnPrem` means "only on-prem MCP servers", and the call is refused if the server is not `OnPrem`. Findings go to `UsageRecord.PiiCategories` as today, so PII incidents automatically include MCP.
- **Residency:** `McpServer.DataResidency`, checked against `VirtualKey.AllowedResidencies`. A server outside the key's residencies is invisible to that key.
- **Circuit breaker:** reuse `ICircuitBreakerStore`, keyed per MCP server. Only transport and 5xx failures count; tool-level `isError: true` results do not.
- **Observability:**
  - OTel spans follow the MCP semantic conventions (`mcp.method.name`, `mcp.session.id`, `gen_ai.tool.name`, ...).
  - New metrics: `ume.gateway.mcp.calls`, `ume.gateway.mcp.call.duration` and `ume.gateway.mcp.listen_streams`, with labels bounded to the server slug, method and outcome.
  - Security events: `McpToolDenied`, `McpToolQuarantined` and `McpServerAuthFailed`.
  - As ADR 003 requires, arguments and results are never persisted.
- **Code Mode:** not now. Sandboxing untrusted code inside the .NET gateway process is a large attack surface, and the token savings only matter with hundreds of tools. When tool count grows, we first add **progressive discovery**: an opt-in `search_tools` / `describe_tool` meta-tool pair that the gateway answers from the catalogue (open point 3).

### Data model

New entities in `Domain/Entities`, with an EF migration and seeding in `DevSeeder`:

```text
McpServer
  Id, Slug (^[a-z][a-z0-9]*(_[a-z0-9]+)*$, at most 24 characters, unique; no `__`, no trailing `_`), DisplayName, Description
  Url, Residency (DataResidency), IsEnabled, OwnerDepartmentId? (null = global)
  AuthType (None | StaticHeaders | OAuthClientCredentials), EncryptedCredential?
                                   -- JSON: header map, or token URL + client id + secret + scope
  AutoApproveNewTools
  ForwardHeaders string[]          -- exact names only
  TimeoutSeconds (default 30), MaxResultBytes (default 1 MiB)
  Status (Healthy | Degraded | Unavailable), LastCheckedAt, LastError?
  Capabilities (tools/resources/prompts/completions flags from server/discover or initialize)
  Instructions? (capped at 4 KiB), CreatedAt

McpTool                            -- discovered snapshot, one row per upstream tool
  McpServerId, Name, Title?, DefinitionHash, Definition jsonb (description, inputSchema, outputSchema, annotations)
  ApprovalState (Pending | Approved | Rejected | Changed)
  ApprovedDefinition jsonb?        -- what clients actually see
  ExposedName                      -- computed, see Naming
  PriceSek?, DiscoveredAt, ApprovedAt?, ApprovedBy?

VirtualKeyMcpGrant                 -- one row per (key, server)
  VirtualKeyId, McpServerId
  AllowedTools string[]            -- ["*"] or explicit names; empty = none
  ApprovalPolicy (Always | Never | NeverForReadOnly)   -- used on the LLM path
  CreatedAt
```

- Grants work like `AllowedProviders`: a key with no grants sees no MCP servers (deny by default). There is no `allow_by_default`; a global server is still granted explicitly. We can add grants at team and department level later, but resolved grants can only narrow.
- Everything goes into `CatalogSnapshot` and is invalidated through `InvalidationKind.Config`, so the request path never reads Postgres.
- `ConfigTransfer` export and import must include `McpServer`, `McpTool` (approved definitions) and the grants. Credentials are excluded, as for providers.

### Naming

- **On `/mcp/{slug}` (one server):** tools keep their original upstream names. Nothing is renamed, and no collisions are possible. We recommend this endpoint for most clients.
- **On `/mcp` (aggregated):** a tool is exposed as `{slug}__{tool}`. Characters outside `[A-Za-z0-9_-]` become `_`. If the result is longer than 64 characters, it is cut and given an 8-character hash suffix (weakness 8).
- `McpTool.ExposedName` is computed once at sync and stored, and the reverse lookup is a dictionary in the snapshot (not a linear scan).
- A collision after sanitising is a sync error shown in the admin UI. It is never silently overwritten.
- On the LLM path the same `ExposedName` is used as the function name, so every provider accepts it.

### Tool pinning (protection against tool poisoning)

- A background sync calls `tools/list` on each server. It runs at start-up, every 10 minutes, and when an upstream sends `notifications/tools/list_changed`.
- The sync compares each tool's definition hash with the snapshot:
  - **New tool:** `Pending`. Not exposed until an admin approves it. A per-server toggle `AutoApproveNewTools` can be turned on for trusted internal servers.
  - **Changed description, schema or annotations:** `Changed`. Clients keep seeing the previously approved definition, and calls validate against the approved `inputSchema`. The admin UI shows a diff, and approving it publishes the change and emits `list_changed`.
  - **Removed tool:** hidden at once.
- The sync runs in the AdminApi (the control plane, which already owns writes to config tables). It uses a Redis lock `ume:mcp:sync-lock`, so only one instance syncs. Results reach the gateways through normal config invalidation.

### Endpoints

Add these to `GatewayEndpoints` and the `GatewayEndpoint` enum (`Mcp`, `McpToolCall`):

| Endpoint | Purpose |
|---|---|
| `POST /mcp` | Streamable HTTP MCP server (stateless). Aggregates every server the key is granted. |
| `POST /mcp/{slug}` | The same for one server, with the original tool names. |
| `POST /v1/mcp/tools/call` | Explicit execution for the LLM path. Takes an OpenAI `tool_call`, Responses `function_call` or Anthropic `tool_use` block, and returns a matching `tool` message, `function_call_output` or `tool_result`. Matches Bifrost's `/v1/mcp/tool/execute`, but accepts all three formats. |
| `GET /v1/mcp/tools?format=openai\|responses\|anthropic` | The key's granted tools, as ready-to-send `tools[]` definitions for any format. Callers can then implement client-side MCP without speaking MCP. |

Supported MCP methods on `/mcp`:

- `server/discover`, `subscriptions/listen` (`2026-07-28`);
- `initialize`, `ping` (handshake-era clients, answered statelessly);
- `tools/list`, `tools/call`;
- `resources/list`, `resources/templates/list`, `resources/read`;
- `prompts/list`, `prompts/get`;
- `notifications/cancelled` (accepted; on HTTP the closed request stream cancels the upstream request);
- `notifications/progress` (relayed);
- `completion/complete` (deferred).

URIs from resources in the aggregated view are namespaced as `mcp+ume://{slug}/{original-uri}`. Sampling, elicitation and roots requests sent *from* upstream servers are refused with a JSON-RPC error at first (open point 4).

### Request flow for `tools/call` on `/mcp`

1. `AuthenticateAsync` (shared stage) resolves the virtual key.
2. Resolve the tool through the snapshot: exposed name to (server, tool).
3. Authorisation. The tool must pass every check below, and a single generic `-32602 Tool not found` is returned for any failure, so no details leak:
   - the key's grant on the server includes the tool;
   - the server is enabled and healthy enough;
   - the server's residency is in the key's `AllowedResidencies`;
   - the tool is `Approved`.
4. Rate limit (`McpCallsPerMinute`) in Redis.
5. Validate the arguments against the approved `inputSchema`, then run the PII guard on the arguments.
6. Reserve budget if the tool has a price.
7. Call upstream through the pooled client, with a timeout. Retry once on a transport failure, timeout or 5xx only when the tool cannot do harm twice: `readOnlyHint: true`, `destructiveHint: false` or `idempotentHint: true` (MCP's defaults treat a tool without hints as destructive). A 404 is retried only against a handshake-era upstream session (the session was dropped, so the request never ran); against `2026-07-28` it is a plain error.
8. Apply the PII guard to the result and cap its size (`MaxResultBytes`). Content blocks pass through unchanged.
9. Account the call (`UsageRecord`), reconcile the budget, and emit a span and metrics.

### Gateway-executed MCP on the LLM path (phase 3)

This feature is opt-in per request and uses the providers' standard shapes. It works with **any** upstream model, because the gateway does the MCP part itself.

**`/v1/responses`.** The caller sends:

```json
{ "tools": [ { "type": "mcp", "server_label": "diarium", "allowed_tools": ["search_cases"],
               "require_approval": { "never": { "tool_names": ["search_cases"] } } } ],
  "max_tool_calls": 8 }
```

- `server_label` must match the slug of a granted `McpServer`. `server_url` is **refused** unless it equals that server's registered URL. Arbitrary URLs would be SSRF, and they would also let OpenAI's cloud call our internal servers directly. The gateway **always strips the `mcp` tool before sending upstream**, even for providers that support hosted MCP natively.
- The gateway puts the granted tools in as function tools (using `ExposedName`), and emits `mcp_list_tools` as the first output item.
- When the model calls one of these functions, the gateway executes it (as in the `tools/call` flow above) and emits an `mcp_call` item. It then continues the conversation with the same provider.
- If approval is required by both the request's `require_approval` and the key grant's `ApprovalPolicy`, the gateway stops and returns `mcp_approval_request`. The client continues with an `mcp_approval_response` input item. **The stricter of request and key policy wins.**
- **Streaming:** the gateway emits `response.mcp_list_tools.*` and `response.mcp_call.*` events between turns.
- **Fallback:** only the first turn can fall back to another provider (before any bytes are sent). Later turns are pinned to the provider that served the first.
- **Limits:**
  - `max_tool_calls` defaults to 10 and is capped per key.
  - There is a wall-clock limit for the whole request.
  - Reaching a limit returns `status: "incomplete"` with a reason. Unexecuted calls are never returned silently (weakness 3).
- **Accounting:** each provider turn and each tool call is its own `UsageRecord`, linked by `ParentRequestId`. Budget is reserved per turn, so a long loop cannot overrun the budget by more than one turn.

**`/v1/messages` (Anthropic connector shape).** The caller sends `mcp_servers` / `mcp_toolset` referencing a gateway slug. The gateway executes the tools and emits `mcp_tool_use` / `mcp_tool_result` blocks, with the same rules as above. It never forwards the connector to Anthropic.

**`/v1/chat/completions`.** No standard shape exists, so there is no built-in loop at first. Callers use `GET /v1/mcp/tools` plus `POST /v1/mcp/tools/call` (client-side loop), or switch to `/v1/responses`. Open point 2 covers a possible extension.

MCP tool definitions stay invisible to routing conditions, as `tools` already are. MCP server `instructions` are added only if the request sets `include_instructions` on the tool. They go in as a separate developer message, never by rewriting the caller's system prompt (prompt caching).

### Admin API and UI

- **API:**
  - `/api/mcp/servers` (CRUD);
  - `/api/mcp/servers/{id}/test` (connect and list tools without saving);
  - `/api/mcp/servers/{id}/sync`;
  - `/api/mcp/servers/{id}/tools` (list, approve, reject, view diff);
  - grants under `/api/keys/{id}/mcp-grants`.

  These follow the existing patterns: thin endpoints, `ApiFaultException` with Swedish messages, audit through `AuditSnapshots`, and `OwnerScopeResolver`, so department admins can manage their own servers and grants.
- **SSRF:** on create, test and sync, the server URL is checked. Link-local and cloud-metadata ranges are always refused. Private ranges are allowed, because municipal servers are internal, but only for `gateway-admin`.
- **UI** (Vue, new area `components/mcp`):
  - an "MCP servers" page with status, tools, approval queue and diff view;
  - an MCP tab on the key detail page with grants and approval policy;
  - MCP filters on Usage and PII incidents;
  - a "Getting started" section with client config snippets for Claude Desktop, Claude Code, VS Code and Cursor;
  - stability per MCP server on the Provider stability page.
- **DataApi:** MCP fields on usage detail and aggregates behind the existing permissions. `catalog.read` gains the MCP servers and tools (approved definitions only).

### Why this is better than Bifrost

| Area | Bifrost | Ume gateway |
|---|---|---|
| Proxy fidelity | Text only, base64 in strings, structured output lost | Content blocks and `structuredContent` passed through. Resources, prompts, progress, cancel and `list_changed`. |
| Sessions | Stateless POST, stub GET | Stateless for every client, so any instance serves any request; `list_changed` on `subscriptions/listen` across instances |
| Agent API | Invented and lossy, no streaming, per-server policy | Standard Responses and Anthropic MCP shapes, streaming, per-request plus per-key approval, explicit `incomplete` |
| Tool injection | Everything into every request by default | Only when the request asks. Zero cost otherwise. |
| Tool trust | Descriptions applied silently | Pinned definitions, approval queue, diffs |
| Governance | Plugin-dependent, header-widening possible | One authorisation stage in the core pipeline, deny by default, residency-aware |
| Data protection | Not addressed | PII policy on arguments and results, `OnPrem`-only routing for tools |
| Stdio | Subprocess in the gateway | Isolated hardened container per server |
| Naming | Unbounded `client-tool` | Original names per server, `slug__tool` capped at 64 when aggregated |
| Accounting | Separate MCP log table | Same `UsageRecord` and budgets as LLM traffic, so DataApi and BI work unchanged |

### Open points for later steps

1. **OAuth 2.1 for inbound clients** (RFC 9728 metadata, DCR, PKCE), with our Keycloak as the authorization server. This lets users connect with SSO instead of pasting a key, and enables per-user upstream credentials and token exchange (RFC 8693) to systems that need the employee's identity.
2. **A chat completions loop** through an explicit extension parameter (`ume_mcp: { servers: [...] }`) that returns all intermediate `tool_calls` / `tool` messages in a `ume_mcp_trace` field. Do this only if real clients ask for it.
3. **Progressive discovery** (`search_tools` / `describe_tool`) for keys with many tools. Revisit sandboxed code execution only if that is not enough, and then out of process.
4. **Upstream sampling and elicitation:** sampling requests routed back through our own LLM pipeline under the caller's key and budget; elicitation relayed to the client.
5. **Routing-rule variables** `mcp_servers` and `mcp_tool_count`, so rules can steer agentic requests (for example to a model that is good at tools).
6. **Virtual MCP bundles** (curated tool sets across servers under one slug), if per-server grants turn out to be too coarse.
7. **Response-side output guardrails** for tool results beyond PII (prompt-injection heuristics).

### Steps and checklist

**1. Foundations**
- [x] ADR 016 "MCP gateway" in `docs/adr.md`. Update the scope table in `docs/architecture.md` and add the `mcp` network.
- [x] Add the C# MCP SDK to `Directory.Packages.props`. Spike: a low-level proxy handler, plus an upstream client against a WireMock / in-test MCP server.
- [x] Entities `McpServer`, `McpTool` and `VirtualKeyMcpGrant`, and `UsageRecord` columns. Migration, DML grants, `DevSeeder` (a demo MCP server in `FakeLlm` or a new `Ume.LlmGateway.FakeMcp` wired into the AppHost).
- [x] `CatalogSnapshot` gains MCP servers, tools and grants, with indexes. Invalidation tests.
- [x] Redis keys and channel added to `RedisKeys.cs` and `deploy/redis/users.acl.example`.

**2. MCP gateway (proxy)**
- [x] `/mcp` and `/mcp/{slug}`, stateless for every client (`HttpServerSessionMode.Stateless`): `server/discover`, per-request `_meta`, and `initialize`/`ping` for handshake-era clients; `tools/list` and `tools/call` (raw JSON passthrough of results).
- [x] The authorisation stage (grant, residency, approval state). Unknown and forbidden tools give the same error.
- [x] Upstream session pool with re-initialize, timeout, retry rules and circuit breaker.
- [x] PII on arguments and results, `McpCallsPerMinute`, budget reservation for priced tools, `UsageRecord` accounting.
- [x] Resources, prompts, progress, cancellation, and change notifications with `list_changed` on `subscriptions/listen` (per-key stream cap).
- [x] Spans, metrics and security events (`gateway.mcp.tool_denied`; `McpToolQuarantined` belongs to the sync in step 3, `McpServerAuthFailed` is deferred, see below).
- [x] Tests: integration with Testcontainers and a fake MCP server, covering grants, denial, PII redaction in results, image content passed through, any instance serves any request (two gateway instances), and destructive tools not retried. Benchmark that `/v1/chat/completions` latency is unchanged.

Step 2b (resources, prompts, progress, listen) notes and deferrals:
- Resources and prompts need their own grant flags (`AllowResources`, `AllowPrompts`, default off) and the server's capability flag; a tool grant exposes no documents.
- Lists are forwarded one server page per request (no catalogue, no caching); the aggregated cursor encodes the server slug and its cursor. A failing server is left out of aggregated lists, with a warning and the failure counted in `ume.gateway.mcp.calls`.
- In the aggregated view the `uri` of `resource_link` and embedded `resource` blocks in tool results and prompt messages is namespaced too, so returned links can be read through `/mcp`; `structuredContent` is left unchanged.
- `resources/read` and `prompts/get` are accounted like tool calls (no price). The read URI is not PII-scanned (redacting it would break the read) and not stored.
- Progress is relayed on an SSE answer with the caller's token; the SDK client delivers upstream notifications concurrently, so late or out-of-order progress is dropped rather than reordered.
- Cancellation: the request-aborted token cancels the upstream request through the SDK client (tested: the upstream handler is cancelled). Client `notifications/cancelled` is accepted and ignored, since on HTTP closing the stream is the signal.
- `subscriptions/listen` compares fingerprints of what the key may see after each invalidation and every `ListenCheckSeconds`; it ends on key revocation or shutdown. The admin API does not publish `ume:mcp:events` yet (step 3), so config invalidation is the trigger.
- Deferred: `completion/complete` (little value through a gateway so far), resource subscriptions (`resources/updated`), noticing upstream changes to prompt and resource lists, and announcing circuit changes.

Step 2a (tools) notes and deferrals:
- The gateway implements the Streamable HTTP POST handling itself instead of the SDK's `MapMcp`, so authentication, error format, middleware and accounting stay in the gateway pipeline; the SDK is used for the upstream client and its protocol constants. Answers are `application/json`; `GET`/`DELETE` answer 405.
- Argument validation is a built-in subset of JSON Schema (`McpSchemaValidator`): JsonSchema.Net is under the Open Source Maintenance Fee EULA (fees for commercial use), and the upstream validates fully anyway.
- Upstream requests the SDK client sends by itself (version probe, `initialize`) carry only the server's credential; caller headers are attached by a per-call context that only the caller's own `tools/call` request can claim.
- Deferred: the `McpServerAuthFailed` security event (upstream 401/403 is logged and answered `-32003`); a per-server price separate from per-tool `PriceSek`; forwarding caller `_meta` (progress tokens) and upstream notifications (2b); limiting the SDK client's buffering before the byte limit applies (the response stream is cut at twice `MaxResultBytes` plus 64 KiB).

**3. Control plane**
- [x] The tool sync worker with a Redis lock, hashing and the approval states (upstream `list_changed` deferred, see below).
- [x] Admin API endpoints, audit and `ConfigTransfer`.
- [x] Admin UI: MCP servers, approval queue with diff, key grants, usage filters, getting started snippets. Vitest and a Playwright e2e test.
- [x] Stdio bridge container pattern documented, with hardening in `deploy/` (compose snippet and runbook).

Step 3b (admin UI and stdio bridge) notes and deferrals:
- *Routing > MCP servers* (gateway and department admins): list with status, last sync and error, owner, residency, tool counts and a review badge; a drawer with *Tools* (sync, review dialog with the structured diff, approve with the reviewed hash and a 409 that reloads and says why, reject, price) and *Settings* (every field, live slug check, write-only credentials with set/not set and replace, test connection saved or unsaved, enable/disable and delete behind confirmations). Global servers are read-only for department admins. The nav item counts tools waiting for review from `GET /api/mcp/servers` (no new endpoint).
- Keys: *MCP servers* section in the key drawer edits grants (all tools or a pick list from the server's tools, approval policy, documents, prompts, unknown-name warnings); the key form sends `mcpCallsPerMinute` on create and edit, so editing a key keeps its MCP limit. `KeyFormDrawer` is the only client of `PUT /api/keys/{id}`.
- Usage and PII incidents: a *Requests* filter (all, model requests, MCP tool calls) backed by a new `traffic=llm|mcp` query parameter; the usage drawer shows an MCP call's server and tool instead of routing details. *Operations* lists MCP servers with their last sync and forces their circuit. *Getting started* has Claude Code, VS Code and Claude Desktop (`mcp-remote`) snippets.
- Stdio bridge: `deploy/mcp/` (supergateway 4.1.0, MIT, digest-pinned, server installed at build time, logging off, behind an nginx TLS sidecar that checks the key because supergateway 4.1.0 has no inbound key check). Verified locally: the image builds, runs non-root and read-only, answers with the key and refuses without it (401). The `mcp` network is added by that extra Compose file, not by the base or hardening files (adopter step), so `build-portainer-stack.sh` and CI checks are unchanged.
- The Playwright test (`tests/e2e/specs/mcp.spec.ts`) needs the Aspire stack with Keycloak (`scripts/Start-E2E.ps1`); it was type-checked, not run.
- Deferred: an Overview card for pending approvals (the nav badge covers it), MCP rows on *Provider stability* (MCP calls carry no provider), and per-server price.

Step 3a (admin API and tool sync) notes and deferrals:
- Removed tools are deleted, not marked: they are hidden at once without a new state for the gateway's `IsExposed`, and a tool that comes back is new and needs approval again (its price too). Exposed-name collisions skip the later tool and set the server `Degraded` with the names in `LastError` (no per-tool error column).
- A definition is `title`, `description`, `inputSchema`, `outputSchema` and `annotations` (`title` added to the plan's list, since clients show it); its hash is canonical (`McpToolDefinition.Hash`, members sorted), shared with the dev seed. `McpTool.Title` follows the approved definition.
- Approving takes the reviewed `definitionHash` (409 if the sync changed it since); `ApprovalState` and `DefinitionHash` are concurrency tokens (migration `McpToolConcurrency`, model only). Rejecting drops an earlier approval.
- Grants are replaced per key on the servers the caller can see; unknown tool names are kept and reported (`unknownTools`), not refused: tools come and go with syncs, and a name allows nothing until the server offers it. Grants are not part of the configuration document (key ids differ), unlike the data-model note above.
- `ume:mcp:events` is not published: `subscriptions/listen` compares fingerprints after every configuration invalidation, which every admin change and every sync that changed something publishes.
- SSRF: DNS is resolved when a URL is saved, tested or synced; every MCP and token connection of the gateway and the admin API refuses forbidden addresses after DNS (`McpAddressGuard.ConnectAsync`), so DNS rebinding cannot reach link-local, metadata or reserved addresses. The private-network rule is only checked when a URL is saved (documented limit). Probes use their own OAuth token cache, so testing an unsaved server leaves no token behind. The admin API uses the gateway's Redis ACL user; the lock needs `set`, `eval`, `get`, `pexpire` and `del`, which it already has.
- `McpServerAuthFailed` is raised by the sync only; the gateway still only logs upstream 401/403 (as deferred in step 2a).
- `McpCallsPerMinute` follows `RequestsPerMinute` (omitted on `PUT /api/keys/{id}` = no limit), so the key form in step 3b must send it.
- Deferred: syncing on upstream `notifications/tools/list_changed` (needs a standing connection per server; the 10-minute and on-demand syncs cover it), `ume:mcp:events`, and returning non-secret credential parts (header names, OAuth client id) for the UI.

**4. LLM path**
- [x] `GET /v1/mcp/tools` and `POST /v1/mcp/tools/call` in all three formats.
- [x] `/v1/responses` hosted `mcp` tool: non-streaming loop, approval flow, `max_tool_calls`, `incomplete` (with provider pinning after the first turn and per-turn budget reservation).
- [ ] Streaming events (4c).
- [ ] Multimodal tool results mapped to native provider parts (OpenAI-compatible and Anthropic).
- [x] `/v1/messages` MCP connector shape (4d, non-streaming; streaming refused with 400 for now).

Step 4a (`/v1/mcp/tools`, `/v1/mcp/tools/call`) notes and deferrals:
- The listing uses `tools/list`'s visibility (`VisibleToolsAsync`) on the aggregated view only (exposed names); it is not accounted, and its authentication failures are recorded under `McpToolCall` (no new `GatewayEndpoint`). Answer: `{ object: "list", format, data }`, `data` being the `tools` array as is (no `ume` extension members, which providers would reject). Responses tools get `strict: false`, since Responses defaults to strict and MCP schemas rarely satisfy it; Chat Completions and Anthropic get no `strict`. The description falls back to the title.
- A call runs the same `CallToolAsync` core as `tools/call` on `/mcp`; the answer is abstracted (`IMcpCallAnswer`: `McpRpcAnswer` for JSON-RPC, `McpToolCallAnswer` for REST), so authorisation, schema check, rate limit, PII, budget, upstream, circuit and accounting are not duplicated. REST answers use the status the usage record already had on `/mcp`; a result blocked by the PII policy is 400 `pii_blocked` (on `/mcp` it stays a JSON-RPC error recorded with 200). An upstream JSON-RPC error is 400 `provider_rejected`. Body errors (not one of the three shapes, no id or name, bad JSON, 413, 415) are accounted like other `/v1` refusals.
- Content mapping: images are native in Responses (`input_image` with a `data:` URL; `function_call_output.output` takes an array of input parts) and Anthropic (`image` blocks), only for PNG, JPEG, GIF and WebP; Chat Completions tool messages take text only, so images and audio become `[image: mime, N bytes]` placeholders. `structuredContent` becomes JSON text only when there is no text block. `isError` is Anthropic `is_error`, otherwise an `Error: ` prefix.
- `x-ume-parent-request-id`: 1–64 characters of letters, digits, `_ - . :`; not checked against the key's own requests (no Postgres on the request path).
- Deferred: a `server` filter on the listing; the Anthropic error body shape for Anthropic-format calls (all errors use the OpenAI-style body of `/v1`); `ParentRequestId` in the admin API's usage views.

### Step 4b design: hosted `mcp` tool on `/v1/responses` (non-streaming)

Decided 2026-10-12 and implemented; the decisions below override the design text where they differ.

Decisions:
1. **Defer, never store results.** If any MCP call of a turn needs approval, none of that turn's MCP calls run: the ones needing approval become `mcp_approval_request`, the others `mcp_call` with `status: "calling"`, and both run on continuation (denied ones get the denial output). The same rule covers a turn that also calls a caller function, and every limit stop (calls `Not run: <reason>`), so the provider is never owed a tool result at a stop. The stored state holds arguments only.
2. **Encryption.** The Redis state is encrypted with Data Protection, purpose `Ume.LlmGateway.McpLoopState.v1` plus the key id (a value only opens for its key).
3. **Reasons.** `max_tool_calls`, `max_turns`, `time_limit`, `budget_exceeded`, `provider_error` are documented in `docs/mcp.md` as gateway extensions; a provider's own reason (`max_output_tokens`) is kept unchanged.
4. **Provider failure after turn 1:** HTTP 200 `incomplete`/`provider_error` with the partial output.
5. **Tool-level refusals** are fed back to the model; budget exhaustion is not: a refused next-turn reservation stops at once, and a tool call whose price does not fit lets the model have the turn already reserved and then stops with `budget_exceeded`.
6. The FakeLlm script file is linked into Gateway.Tests.
7. PII as designed, with a test that a Redact key's tool-result PII never reaches turn 2 and that *Reroute to on-prem* withholds it.
- `include: reasoning.encrypted_content` only for `OpenAIReasoning` deployments with `store: false`.

Implementation notes and deviations:
- **`AttemptAsync` is not split.** An extra awaited async method on the normal path costs one state-machine allocation per request, so the MCP path has its own candidate loop (`SendTurnAsync`) built from the same helpers (`TryGetCredential`, `RecordFailureAsync`, `RecordSuccessAsync`, `RequestRewriter.Apply`). The normal path gains one enum comparison and a null check; benchmarks before and after are below.
- Benchmarks (Release, Docker; `*Pipeline*`, `*Request*`, `audit`) before and after: Postgres statements and Redis commands per request unchanged (chat 0 / 1 / 9, cache miss 11 / 1 / 9); `RequestBenchmarks` allocations identical (888 B, 1 464 B, 848 B small; 70 336 B, 67 096 B, 125 936 B large); `PipelineBenchmarks` allocations within run-to-run noise (chat in memory 72.51 → 73.14 KB, Redis 88.62 → 88.68 KB; stream in memory 611.3 → 610.4 KB); 64-client streaming load 4 342 → 4 488 req/s.
- The `mcp` tools are validated and the input translated before the PII guard; the function tools are added when turn 1 is sent, so the PII guard and token estimate see the caller's body, not tool descriptions (a description with an e-mail example would otherwise block a *Block* key).
- Limits are checked, and the next turn's budget reserved, after a turn arrives and before its calls run; the time limit is not enforced inside a single call (the server's own timeout bounds it).
- A function call to a name that is neither an offered MCP tool nor one of the caller's `function` tools is answered to the model with `Error: Unknown tool: '<name>'`; it counts against `max_tool_calls` and is neither shown to the client nor a usage record. Items of the provider's own hosted tools (`web_search_call`, …) pass through untouched. `max_tool_calls` counts per HTTP request; a continuation starts a fresh count.
- A `resp_ume_` continuation is pinned to the stored deployment (409 `mcp_continuation_not_found` when it is no longer a candidate); a continuation by replaying history is routed normally. Its first turn records the original request as `ParentRequestId`.
- State is read when the request is prepared (validation, tamper check) and taken atomically just before the loop runs, so a request refused by a later stage (rate limit, PII, budget) does not use it up; two racing copies get 409 for the loser.
- Limitation: after `provider_error` the provider never got the last tool outputs, so that response cannot be continued with `previous_response_id`; the client has to send the history back (the `mcp_call` items carry the outputs).
- `mcp_call.output` is text (image placeholders as in the 4a Chat mapping); the model got native image parts. Replaying such an item sends the text.
- Fixed on the way: `ProgressRelay` could write two progress notifications out of order (check and write were not atomic); the existing progress test was flaky on `main` for this reason.

Original design:

**Detection and entry.** `HandleAsync` checks one condition, only for `GatewayEndpoint.Responses`, right after the body is parsed: `tools` has an entry with `type: "mcp"`, or `previous_response_id` starts with `resp_ume_` (a gateway continuation, see *Approval*). The check is a scan over `tools`, not `input`. If the condition is false, nothing changes and the request takes the current path. If it is true, the request goes to `HandleResponsesMcpAsync` (new partial `GatewayRequestHandler.ResponsesMcp.cs`). That handler reuses the stages in order: model resolution, attachments, rate limit, PII, routing, route selection, budget. It replaces only the success branch of `AttemptAsync`. To do that, `AttemptAsync` is split so that one provider call that does not write the response (`SendTurnAsync` → `ProviderJsonResult` or failure, with the existing fallback loop) can be used by both paths. The normal path keeps the same behaviour and the same allocations; I will check this with the `*Pipeline*` benchmark. `stream: true`, `background: true` or `conversation` together with an `mcp` tool gets 400 `invalid_request`. Streaming is planned for 4c. Background mode and Conversations are not supported, because a gateway-run loop would duplicate items into a provider conversation.

**The `mcp` tool.** It is validated after authentication, against the snapshot:
- `server_label` must be a server slug the key may use (`McpAccess.CheckServer`). An unknown server and one the key may not use get the same answer: 404 `mcp_server_not_found`.
- `server_url` must be absent or exactly the registered URL. Anything else gets 400. We never connect to a URL the caller supplies.
- `allowed_tools` is either an array of names or `{tool_names:[…]}`. It names server tool names and can only narrow the tools `VisibleToolsAsync` shows the key. `read_only: true` inside it is honoured, using `readOnlyHint`.
- `require_approval` is `"always"`, `"never"`, `{always:{tool_names}}`, `{never:{tool_names}}`, or both objects together. A tool that none of them names needs approval, as in OpenAI's default. When the field is absent, everything needs approval.
- `headers`, `authorization` and `connector_id` get 400: the gateway uses the server's own credential. Caller headers still reach a server only through its `ForwardHeaders`, taken from the HTTP request.
- Two `mcp` tools with the same `server_label` get 400.

**Rewriting.** Every `mcp` tool is replaced in place by the function tools it offers (`McpToolFormats.Definition(Responses, ExposedName, …)`, with `strict:false`). A caller function tool whose name equals an offered exposed name gets 400. `tool_choice: {type:"mcp", server_label, name}` becomes `{type:"function", name:<exposed>}`. An `mcp` entry inside `tool_choice.allowed_tools` is expanded the same way. `max_tool_calls` is removed from the body: OpenAI applies it only to built-in tools, and other providers may reject it. The gateway enforces it itself. The `mcp` tool is never sent upstream, whatever the provider's capabilities.

**Input items from earlier responses** (a client that replays history instead of using `previous_response_id`):
- `mcp_list_tools` is dropped.
- `mcp_call` becomes `function_call` plus `function_call_output`. The `call_id` is the `mcp_call` `id`, the name is `McpToolNames.Exposed(server_label, name)`, and the output is the `output`, or `error` with the `Error: ` prefix.
- `mcp_approval_request` plus `mcp_approval_response` are resolved as described under *Approval*.
- An `mcp_*` item whose `server_label` is not one of this request's `mcp` tools gets 400.

**The loop** (`McpResponsesLoop`). Turn N+1 always replays: it sends the body of turn N with `input` = the input of turn N, followed by the output items of turn N exactly as returned (reasoning items included), followed by one `function_call_output` per MCP call. Replay is chosen for every request, not per request:
- It works with `store: false`, where there is nothing to chain to.
- It works with providers whose Responses implementation has no `previous_response_id`.
- It leaves the caller's own `previous_response_id`, `store` and `instructions` semantics exactly as sent. They are resent unchanged on every turn, so the provider sees the same prior context each time.
- It costs the same input tokens as chaining. The provider bills the whole context either way, and caching helps both.

When `store` is false and the pinned deployment has `ParameterProfile.OpenAIReasoning`, `include` gains `reasoning.encrypted_content`, so that replayed reasoning items stay usable. If the caller did not ask for it, it is removed from the final response.

On turns ≥2:
- `tool_choice` set to `"required"`, a named function or a forced MCP tool becomes `"auto"`. Otherwise a forced call would repeat forever.
- `parallel_tool_calls` and every other parameter are passed through unchanged.

After each turn:
- If the provider `status` is not `completed` (for example `incomplete`/`max_output_tokens`), the loop stops and that status and reason are returned.
- If there are no function calls, the loop is done.
- MCP calls in that turn (exposed names offered in this request) are executed in order through the shared core (`CallToolAsync` with a third answer type, `McpLoopAnswer`, which records the result or error instead of writing HTTP). Calls that may run do run; calls that need approval wait.
- If the turn also has a call to one of the caller's own function tools, its MCP calls are still executed, and the loop then stops. The caller's `function_call` items are returned unchanged, for the caller to answer.
- A function call to a name that is neither offered nor the caller's gets an error output (`Error: Unknown tool`) and the loop continues.

**Final response.** The base is the last turn's response object (model, `created_at` and so on). Changes:
- `tools` and `tool_choice` are the caller's originals. `max_tool_calls` is echoed.
- `usage` is the sum over turns, including `input_tokens_details.cached_tokens` and `output_tokens_details.reasoning_tokens`.
- `output`, in order:
  1. one `mcp_list_tools` item per `server_label` (`id` `mcpl_…`, `server_label`, `tools: [{name, description, input_schema, annotations}]` with server names). It is omitted when the request has a `previous_response_id` or its input already holds `mcp_list_tools` for that label (the model already saw the tools in an earlier response);
  2. then, turn by turn, the model's items, with each MCP `function_call` replaced by `mcp_call` `{id: <provider call_id>, type, server_label, name: <server's tool name>, arguments: <exactly as the model produced them>, output: <text>, error: <text|null>}`. `output` is a string: text with image placeholders, as in the 4a Chat mapping. The model itself received native image parts. An `mcp_approval_request` stands in place of each call that is waiting.
- `id`:
  - When the provider holds no unanswered call, `id` is the last turn's provider id. With `store` on, that stored response contains the whole replayed history, so a later `previous_response_id` works at the provider without the gateway.
  - When the loop stops with MCP calls the provider has not had outputs for, and `store` is not false, `id` is `resp_ume_<random>`. Those calls are the approval-pending calls, calls left unrun by a limit, and auto-run calls in a turn that also needs approval or caller calls. Redis maps the gateway id to the provider id, the pinned deployment and the outputs still owed (see below).
  - With `store:false` the provider id is returned. Clients replay, and the items carry everything that is needed.
- `status`/`incomplete_details`:
  - `completed`;
  - `incomplete` with the provider's own reason;
  - `incomplete` with one of our reasons: `max_tool_calls`, `max_turns`, `time_limit`, `budget_exceeded`, `provider_error`. These reasons are gateway extensions to the OpenAI set.
  - When approval is needed, `status` stays `completed`, as OpenAI does for `mcp_approval_request`.
  - Calls that were not run because of a limit appear as `mcp_call` with `error: "Not run: <limit> reached"`. They are never silently dropped.

**Approval.** A call needs approval when the request or the grant asks for it, whichever is stricter. The grant asks for approval when its `ApprovalPolicy` is `Always`, or when it is `NeverForReadOnly` and the approved definition has no `readOnlyHint: true`. The request asks per `require_approval`. When a turn has such calls:
1. The calls that may run are executed first.
2. Each waiting call becomes `mcp_approval_request` `{id: "mcpr_<128-bit random>", server_label, name, arguments}`.
3. The loop stops.

State in Redis:
- `ume:mcp:approval:{keyId:N}:{id}`: server, tool, exposed name, provider `call_id`, a SHA-256 of the arguments, the arguments themselves, and the pending id.
- `ume:mcp:pending:{keyId:N}:{resp_ume id}` (store mode only): provider response id, deployment id, and the outputs owed for the other calls of that turn (the results of auto-run calls, and "Not run" texts).

The payload is encrypted with a Data Protection purpose (`Ume.Mcp.LoopState`). It has to hold the arguments: a `previous_response_id` continuation sends only `mcp_approval_response`, and the gateway cannot read stored responses at every provider. The TTL is `Gateway:Mcp:Loop:ApprovalTtlMinutes` (60). Records are read and deleted atomically, with a Lua `GET`+`DEL` through `eval`, which the ACL already allows. A record is used once, and a lookup with another key finds nothing.

On continuation, for each `mcp_approval_response`:
- the approval record is looked up. An unknown, expired or already-used id gets 400 `mcp_approval_not_found`;
- `approve:true` executes the stored arguments;
- `approve:false` sends a `function_call_output` "The user denied this tool call" plus the `reason`, if one was given.

In replay mode the resent `mcp_approval_request` item must match the stored name and argument hash, otherwise 400. The stored arguments are what run, so the arguments that run are exactly what the model produced.

In store mode the continuation's `previous_response_id: resp_ume_…` is replaced by the provider id. The owed outputs from the pending record are prepended, the request is pinned to the stored deployment (routing and fallback are skipped), and the loop goes on. The continuation is a new HTTP request, so it is accounted with its own `ParentRequestId` chain.

**Limits** (gateway options under `Gateway:Mcp:Loop`):
- `max_tool_calls`: default `DefaultMaxToolCalls` (10). A request may set it lower, or higher up to `MaxToolCalls` (50); above that it is capped. It counts the MCP calls the gateway executes.
- `MaxTurns`: 20, a safety net.
- `TimeoutSeconds`: 300, wall clock from turn 1. Checked before each turn and each tool call, and passed as a deadline to the tool core.

A per-key cap can be added later as a key field. Every limit ends the loop through the `incomplete` path described above.

**Governance and accounting.**
- Turn 1 is the request as today: request id `x-request-id`, RPM and TPM, the PII guard on the caller's body, routing rules, fallback, budget reservation. Its turn records no `ParentRequestId`.
- Turns ≥2 each get their own `RequestState` (a new `req_…` id, `ParentRequestId` = the turn-1 id, `Endpoint = Responses`, the same requested model). Each has a budget reservation for its own estimate (body length plus max output). If budget refuses, the loop stops with `incomplete`/`budget_exceeded`. Turns ≥2 do not count against RPM; their tokens are recorded against TPM when they are accounted (`RecordTokensAsync`, as today), with no pre-check.
- The body is not PII-scanned again on turns ≥2. Their only new content is model output (not scanned on any endpoint) and tool results, which the MCP core has already put through the key's policy. One extra rule: with `RerouteToOnPrem`, a tool result in which PII was found is withheld from a pinned provider that is not `OnPrem`. The model gets `Error: result withheld (personal data, provider not on-prem)`.
- Turns ≥2 are pinned to turn 1's deployment, with no routing and no fallback. A failure there ends the loop with `incomplete`/`provider_error` and HTTP 200, keeping what was produced, because executed tools may have had side effects that the client must see. The failed turn is accounted as `ProviderError`.
- Each tool call goes through the shared MCP core with `Endpoint = McpToolCall` (the "MCP on the LLM path" bucket, already counted by `traffic=mcp`), `ParentRequestId` = turn-1 id, its own `req_…` id, `McpCallsPerMinute`, price and budget. Tool-level refusals and failures do not stop the loop: unknown, rate limited, PII blocked, invalid arguments, upstream failure, too large and budget. They become an `Error: …` output for the model and the `mcp_call.error` for the client, like OpenAI's hosted MCP. The core's HTTP status stays in the usage record.
- Client disconnect: the request token cancels the turn or tool call in flight, which is accounted 499 by its own state. No further turn starts. Every turn and every tool call has its own `RequestState`, so each is accounted exactly once.

**Response headers.**
- `x-ume-provider`, `-model`, `-residency`, `-fallbacks` and `-rule`, and the rate-limit headers: from turn 1, the pinned deployment.
- `x-ume-cost-sek`: all turns plus tool prices.
- `x-ume-budget-remaining-sek`: after the last reservation.
- `x-ume-pii`: the strongest action of turn 1 and the tool calls.
- New `x-ume-turns`: the number of provider turns.

**Tests.** FakeLlm `/v1/responses` gets a deterministic script, in its own file `FakeResponsesScript.cs`. The file is linked into `Gateway.Tests` (`<Compile Include … Link>`) and served there as a WireMock callback, so the loop runs against the same script in unit and integration tests. The script:
- If the newest user text has `[[call:NAME {json}]]` (several mean parallel calls) and the input has no `function_call_output` for them yet, it returns those `function_call`s.
- Once outputs are present, it answers with a message quoting them.
- `[[loop:NAME]]` calls `NAME` on every turn, for the limit tests.
- `[[fail-turn:N]]` returns 500 on turn N, to test pinning and `provider_error`.
- Reasoning items are emitted for `fake-reasoning`, to check that replay keeps them.

Gateway.Tests (McpUpstreamServer plus the script) cover:
- detection and a no-mcp parity check;
- validation errors;
- tool rewriting and conflicts;
- the loop with mixed caller and MCP calls;
- output order and `mcp_call` shape, and summed usage;
- the approval round trip in store mode (`resp_ume_` plus Redis) and in replay mode, including denial, tampered arguments, reuse, another key and expiry;
- each limit giving `incomplete`;
- turn-2 failure with pinning;
- usage records: turns with a parent, tools as `McpToolCall`, budget per turn;
- disconnect;
- `stream:true` giving 400.

IntegrationTests get one end-to-end run with FakeLlm and the demo MCP server.

**5. Docs and release**
- [ ] `docs/mcp.md`: concepts, client setup, security model, limits.
- [ ] Update `docs/security-and-compliance.md` (tool pinning, PII on results, SSRF), `docs/runbook.md` (sync failures, quarantined tools), `docs/admin-api.md`, `README.md` and `HANDOVER.md`.
- [ ] `CHANGELOG.md` entries under `## Unreleased`.
