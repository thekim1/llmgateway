# MCP gateway

The gateway exposes the municipality's MCP servers ([Model Context Protocol](https://modelcontextprotocol.io)) to MCP
clients such as Claude Code, VS Code and Claude Desktop, under the same virtual keys, budgets, PII policy and residency rules
as the LLM API. See ADR 016 in [adr.md](adr.md) for the decision.

> **Status.** Tools, resources and prompts work end to end (listing, calling and reading, authorisation, PII guard, rate
> limit, budgets, accounting, circuit breaker, metrics), with progress, cancellation and change notifications
> (`subscriptions/listen`). Clients of the LLM APIs can list the tools as tool definitions and run the calls a model makes
> (see *Using MCP tools with the LLM APIs*); the gateway does not yet run a tool loop itself.
> `completion/complete` and resource subscriptions are not supported yet. Servers, tool approval
> and grants are managed in the admin UI (**Routing > MCP servers**, and **MCP servers** on a key) or the admin API (see
> *Administration*). The local dev seed adds the demo server `demo`, with tools, a resource and a prompt, and grants it all
> to the dev key.

## Endpoints

| Endpoint | What it serves |
|---|---|
| `POST /mcp` | Every MCP server the key is granted. Tools and prompts are named `{slug}__{name}`, resource URIs are `mcp+ume://{slug}/{uri}` (see *Naming*). |
| `POST /mcp/{slug}` | One server, with the server's own tool and prompt names, resource URIs and list cursors. Recommended when a client only needs one server. |
| `GET /v1/mcp/tools` | The key's tools as tool definitions for Chat Completions, Responses or Anthropic Messages (see *Using MCP tools with the LLM APIs*). |
| `POST /v1/mcp/tools/call` | Runs one tool call a model made and answers in the same format. |

The two `/mcp` endpoints speak Streamable HTTP and are **stateless**: the gateway issues no `Mcp-Session-Id`, keeps no session state, and any
gateway instance answers any request. `GET` and `DELETE` answer 405; change notifications come on `subscriptions/listen`.

| Protocol revision | How the client talks to the gateway |
|---|---|
| `2026-07-28` | Version and capabilities in every request's `_meta`; `server/discover` answers versions, capabilities and instructions. |
| `2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05` | `initialize` (answered without a session), `ping`, then requests with `MCP-Protocol-Version`. |

| Method | Answered by | Recorded as usage |
|---|---|---|
| `initialize`, `server/discover`, `ping`, `tools/list` | the gateway's catalogue | no |
| `resources/list`, `resources/templates/list`, `prompts/list` | forwarded to each server the key may use them on, one page per request | no |
| `tools/call`, `resources/read`, `prompts/get` | forwarded to the one server | yes |
| `subscriptions/listen` (`2026-07-28` only) | the gateway (see *Change notifications*) | no |
| notifications (`notifications/initialized`, `notifications/cancelled`, ...) | accepted with 202 | no |
| anything else (`completion/complete`, `resources/subscribe`, `logging/setLevel`) | `-32601` method not found | no |

Answers are `application/json`, except when progress is relayed or for `subscriptions/listen` (SSE).

## Authentication

The virtual key, exactly as for `/v1`: `Authorization: Bearer ume-sk-…`, `x-api-key` or `api-key`. A missing, unknown,
revoked or expired key gets `401` with `WWW-Authenticate: Bearer` and the gateway's usual JSON error; the refusal is recorded
like on `/v1` (`AuthFailures`, security event `gateway.auth.failed`). The key is never sent to an MCP server.

## What a key sees

A tool is listed, and can be called, only when all of these hold. Listing and calling use the same check (`McpAccess`), so
a tool that is not listed cannot be called by name either.

1. The key has a grant for the server, and the grant names the tool (or `*`). A key without grants sees no MCP servers.
2. The server is enabled.
3. The server's residency is in the key's allowed residencies (empty = all, as for providers).
4. The tool is approved (`Approved`, or `Changed`: clients keep seeing the approved definition until the change is approved).
5. The server's circuit is closed.

Calling a tool that fails any check gets the same answer as a tool that does not exist (`-32602 Unknown tool: '<name>'`).
The reason is only in the security event `gateway.mcp.tool_denied` (event id 1004, attribute `Reason`: `not_granted`,
`tool_not_granted`, `server_disabled`, `residency`, `not_approved`, `circuit_open`, `unknown_tool`).

The aggregated `instructions` (from `initialize` or `server/discover`) contain only the instructions of servers the key may
use, each under its name, at most 8 192 characters in all.

## Resources and prompts

A tool grant exposes no documents: resources need the grant's `AllowResources` and prompts its `AllowPrompts` (both off by
default), and the server must offer them (`McpServers.Capabilities`). Otherwise the same checks as for tools apply (enabled,
residency, circuit closed); the capabilities in `initialize`/`server/discover` list resources and prompts only when the key may
use them on some server. Reading a resource or getting a prompt that is unknown or not allowed answers `-32602` like an unknown
one, with the reason in `gateway.mcp.tool_denied` (`resources_not_granted`, `prompts_not_granted`).

- **Lists** are not catalogued: the gateway forwards them to the servers, in slug order, one server page per request. The
  aggregated cursor is opaque and names the server and that server's own cursor. A server that fails is left out of that
  page so the others still answer: the gateway logs a warning (`MCP server <slug> left out of <method> (<failure>, HTTP
  <status>)`, no content), counts it in `ume.gateway.mcp.calls` with the failure as `outcome`, and counts it against the
  server's circuit. On `/mcp/{slug}` cursors pass through unchanged.
- **`resources/read`** and **`prompts/get`** go through the steps of a tool call: rate limit, PII guard, accounting (no
  price), circuit, size limit. The PII guard scans `prompts/get` arguments like tool arguments; the URI of a read is not
  scanned (redacting it would break the read). Results: the `text` of resource contents (not `blob`), and the description and
  message content of a prompt.

## A tool call, step by step

1. Resolve the name (exposed name on `/mcp`, the server's own name on `/mcp/{slug}`) and authorise it (above).
2. Check the arguments against the approved `inputSchema`: `type`, `required`, `properties`, `additionalProperties: false`,
   `items`, `enum`, `const`, string lengths, number bounds and item counts. Other keywords are left to the server.
3. Count the call against the key's `McpCallsPerMinute` (null = no limit).
4. PII guard on the arguments with the key's PII policy: *Redact* redacts them, *Block* refuses the call, *Reroute to
   on-prem* refuses servers that are not `OnPrem`. Every string in the arguments is scanned.
5. Reserve the tool's price (`McpTools.PriceSek`) against the key, team and department budgets, when it has one.
6. Call the server: pooled client per server, the server's `TimeoutSeconds`, its credential, and the caller headers its
   `ForwardHeaders` names. The call is sent once more after a transport failure, a timeout or a 5xx, unless the tool may
   change things: a tool that is not `readOnlyHint: true` counts as destructive unless it says `destructiveHint: false`,
   and a destructive tool is only repeated when it says `idempotentHint: true`.
7. PII guard on the result (text and embedded-resource text in `content`, and `structuredContent`; not base64 data, MIME
   types, URIs or `_meta`): *Redact* redacts it, *Block* replaces it with an error.
8. Answer with the server's `result` unchanged: content blocks of every type, `structuredContent`, `isError` and `_meta`.
   A result larger than the server's `MaxResultBytes` (default 1 MiB) becomes an error.
9. Account it (below), reconcile the budget, record the circuit, metrics and a span.

### Errors

| JSON-RPC code | When |
|---|---|
| `-32602` | Unknown or not allowed tool (`Unknown tool: '<name>'`), or arguments that do not match the schema. |
| `-32000` | `McpCallsPerMinute` reached; `data.retryAfterSeconds`, and the `Retry-After` header. |
| `-32001` | A budget is used up (priced tools only). |
| `-32002` | PII policy: *Block* found personal data in the arguments or the result, or *Reroute to on-prem* and the server is not on-prem. |
| `-32003` | The server could not be reached, timed out, failed or refused the gateway's credential. |
| `-32004` | The result is larger than `MaxResultBytes`. |
| `-32022` | Unsupported protocol version (HTTP 400), with `data.supported`. |
| server's code | The server answered with a JSON-RPC error; it is passed on, its message cut to 500 characters and put through the key's PII policy like a result text. |

A tool that reports its own failure (`isError: true`) is a normal result.

## Naming

On `/mcp` a tool is `{slug}__{tool}`; characters outside `[A-Za-z0-9_-]` become `_`, and a name over 64 characters is cut
and ends in `_` plus 8 hex characters of a hash, so every LLM provider accepts it as a function name. A slug is lower-case
letters and digits with single underscores between them (at most 24 characters, no `__`, no trailing `_`), so the first
`__` always ends the slug. Prompts are named the same way. A resource URI becomes `mcp+ume://{slug}/{uri}` (the server's
URI unchanged after the slug), in lists, templates and read results, and the gateway removes the prefix again on
`resources/read`. The same goes for the `uri` of `resource_link` and embedded `resource` content blocks in `tools/call`
results (top-level `content`) and `prompts/get` messages, so a link a tool returns can be read through `/mcp`; nothing else
in the result changes, and `structuredContent` (the tool's own data) is left as it is. On `/mcp/{slug}` tools, prompts and
URIs keep their names.

## Administration

The admin UI and the admin API ([admin-api.md](admin-api.md#mcp-servers-and-grants)) manage servers, tools and grants.
Gateway admins manage every server; department admins manage the servers their departments own, see global servers
read-only and can grant them to their keys.

In the admin UI:

1. **Register:** *Routing > MCP servers > New MCP server*. Enter a short name (checked as you type), the URL, residency,
   owner and authentication. Credentials are write-only: the form shows only whether one is set, and offers *Replace the
   stored credential*; choosing another authentication type needs new credentials, and *None* removes the stored one.
   *Test connection* connects with the values in the form (saved or not) and lists the tools. Leave *Approve new tools
   automatically* off unless the server is a trusted internal one; the form explains why.
2. **Sync:** after *Create server* the drawer shows the *Tools* tab; *Sync now* reads the tools and reports what changed.
3. **Approve:** select a tool to review it. A *Changed upstream* tool shows each change from the approved definition to the
   one offered now, by JSON path, with the approved and the new value as text. *Approve* approves exactly the definition on
   screen; if the server changed it in the meantime, nothing is approved, the dialog shows the new definition and says so.
   The dialog also sets the price per call. The *MCP servers* menu item shows how many tools wait for review.
4. **Grant:** on the key (*Access > Keys*, select the key), *MCP servers > Edit MCP access*: add the server, keep *All
   approved tools* or pick tools, choose the approval policy and whether documents (resources) and prompts are allowed, and
   save. Tool names the server does not offer are kept with a warning. *MCP calls per minute* is in the key's form, next to
   the other limits.

Usage and PII incidents can be filtered to MCP calls (*Requests: MCP tool calls*); MCP calls show as `mcp:{slug}/{tool}`.
*Operations* lists each MCP server's last sync and lets a gateway admin force its circuit open or closed.

With the admin API:

1. **Register** the server: `POST /api/mcp/servers` with a slug, the URL, residency, auth type and credential. Try it first with
   `POST /api/mcp/servers/test`, which connects and lists the tools without saving anything. The URL must pass the SSRF rules
   (below) and, in production, use https.
2. **Sync** it: `POST /api/mcp/servers/{id}/sync`, or wait for the scheduled sync. Its tools appear as `Pending`.
3. **Review and approve** each tool: `GET /api/mcp/servers/{id}/tools` shows the definition (description, input and output
   schema, annotations); `POST .../tools/{toolId}/approve` with the tool's `definitionHash` approves exactly that definition
   (409 if the server changed it in the meantime). `POST .../reject` hides a tool; `PUT .../price` sets a price per call.
4. **Grant** the server to keys: `PUT /api/keys/{id}/mcp-grants` with the server, the tool names (or `*`), the approval policy
   and whether resources and prompts are allowed.

The gateways pick up every change at once (configuration invalidation); a key's `subscriptions/listen` stream then gets
`list_changed`.

### Tool sync and pinning

The admin API syncs every enabled server at start and every `Admin:McpSyncMinutes` (10), and a server when an admin asks.
One instance syncs at a time: the scheduled sync holds the Redis lock `ume:mcp:sync-lock` (5 minutes, renewed per server); an
admin's sync while it runs gets 409. A sync connects with the gateway's MCP client (`McpUpstream`, a fresh client each time),
reads the capabilities and instructions (cut to 4 096 characters) and every page of `tools/list` (at most 50 pages), and
compares each tool's definition with the stored one by a canonical SHA-256 (`McpToolDefinition.Hash`: members sorted at every
level, so the order Postgres stores `jsonb` in does not matter). A definition is the tool's `title`, `description`,
`inputSchema`, `outputSchema` and `annotations`; `_meta`, icons and other members are not pinned and not passed on.

| Upstream | Result |
|---|---|
| A new tool | `Pending`, not visible; with `autoApproveNewTools` (trusted internal servers only) `Approved` at once, by `system:auto-approve`. |
| An approved tool's definition changed | `Changed`: clients keep the approved definition, calls are checked against its input schema. `GET .../tools` shows the differences; approve the new definition or reject the tool. |
| A changed tool is back to its approved definition | `Approved` again. |
| A pending or rejected tool changed | Its definition is updated; it stays pending or rejected. |
| A tool is gone | The row is deleted, so it is hidden at once and keys' grants naming it allow nothing. If it comes back it is a new tool and needs approval again (its price too). |
| Two tools sanitise to the same exposed name | A stored tool keeps the name (of two new ones, the first by name); the other is left out and the server's `lastError` names both. Nothing is overwritten. |

After a sync the server's `status` is `Healthy`, `Degraded` (some tools were left out; see `lastError`) or `Unavailable`
(the server could not be reached, timed out, refused the credential or failed; the previous tools and capabilities stay as
they were). `lastError` never holds content from the server or a secret. A sync that changed tools, capabilities or
instructions is audited (actor `system:mcp-sync` for scheduled ones) and makes the gateways reload; an admin's sync is
always audited. Security events (category `Ume.LlmGateway.Security`): `admin.mcp.tool_quarantined` (2002, `Reason` `new` or
`changed`) for every tool that waits for an admin, and `admin.mcp.server_auth_failed` (2003) when a server answers 401/403
to the sync. A sync does not react to upstream `notifications/tools/list_changed`; changes are seen at the next sync.

### SSRF rules

The server URL and an OAuth token URL are resolved when they are saved or tested, and refused when any address is
link-local (169.254.0.0/16, fe80::/10, where cloud metadata lives), a cloud metadata address (168.63.129.16,
100.100.100.200, fd00:ec2::254), unspecified, multicast or reserved. Private addresses (loopback, RFC 1918, 100.64.0.0/10,
fc00::/7, fec0::/10), where the municipality's own servers are, need a gateway admin. IPv6 addresses that carry an IPv4
address (IPv4-mapped, IPv4-compatible, NAT64 `64:ff9b::/96`, 6to4 `2002::/16`) count as that IPv4 address. A sync checks the
stored URL again, and every MCP and token connection of the gateway and the admin API checks the addresses it connects to
(`McpAddressGuard`), so a name that later resolves to a forbidden address (DNS rebinding) is never reached. The private
rule is only checked when a URL is saved: a department-owned name that later resolves to a private address is reached, so
such names should be under municipal DNS control.

## MCP servers

| Column | Meaning |
|---|---|
| `Slug`, `DisplayName`, `Description` | Names. |
| `Url` | The server's Streamable HTTP endpoint. Production allows only `https`, like providers. |
| `Residency`, `IsEnabled`, `OwnerDepartmentId` | As for providers. |
| `AuthType`, `EncryptedCredential` | `None`; `StaticHeaders` (credential: a JSON object of header names and values, e.g. `{"X-Api-Key":"…"}`); or `OAuthClientCredentials` (credential: `{"tokenUrl","clientId","clientSecret","scope"}`; the token is fetched with HTTP Basic client authentication and cached until a minute before it expires). Encrypted with Data Protection like provider credentials. |
| `ForwardHeaders` | Caller headers sent on to the server, by exact name. `Authorization`, `x-api-key`, `api-key`, cookies and MCP/HTTP protocol headers are never forwarded. A forwarded header goes only with that caller's own `tools/call`, never with requests the gateway's client sends on its own. |
| `TimeoutSeconds`, `MaxResultBytes` | Per call (default 30 s and 1 MiB). |

Legacy SSE servers and stdio servers are not supported directly; run them behind a bridge (see *Stdio servers*).

### Stdio servers

A server that only speaks stdio (most `npx`/`uvx` MCP servers) runs in its own container behind a bridge that serves it
as Streamable HTTP. `deploy/mcp/compose.mcp-stdio-example.yaml` is a worked example with `mcp-server-time`:

- `mcp-time-bridge`: [supergateway](https://github.com/supercorp-ai/supergateway) (MIT, image `supercorp/supergateway`,
  pinned by digest) with the MCP server installed when the image is built (`deploy/mcp/Dockerfile.stdio-example`), so the
  running container downloads nothing and needs no egress. Non-root, read-only root filesystem, `cap_drop: ALL`,
  `no-new-privileges`, memory, CPU and process limits, and `--logLevel none` (its `info` level logs every message,
  arguments and results included). It is only on its own internal network.
- `mcp-time`: an unprivileged nginx (pinned by digest) that terminates TLS (production gateways call MCP servers over https
  only) with a certificate from the deployment's internal CA, and checks the key: supergateway 4.1.0 checks none itself.
  It is on that network and on the internal `mcp` network, which the example also attaches the gateway and the admin API
  to. It holds only its own certificate and its key list.

To use it, as an adopter step (the base Compose files are unchanged):

1. Issue a certificate for `mcp-time` from the internal CA, as `init-deployment.sh` does for the services, into
   `deploy/certs/mcp-time/mcp-time.crt` and `.key`.
2. Write `deploy/secrets/mcp-time/authorized-keys.map` with one line per accepted key: `"Bearer <random key>" 1;`.
3. Start it with the production files:
   `docker compose -f deploy/compose.prod.yaml -f deploy/mcp/compose.mcp-stdio-example.yaml --env-file deploy/.env up -d --wait`.
4. Register `https://mcp-time:8443/mcp` (residency `OnPrem`, authentication *Static headers*, `Authorization: Bearer <key>`),
   sync it and approve its tools.

For another server, copy the example with its own name, image (installing that server at build time), certificate, key
list and network. Each server gets its own containers: never put two servers, or the server and anything else, in one.

## Progress and cancellation

A `tools/call`, `resources/read` or `prompts/get` with `_meta.progressToken` gets the server's progress notifications,
with the client's token, on an SSE answer that ends with the result. The gateway asks the server with a token of its own,
so callers never see each other's. Progress is best effort: a notification that the upstream client delivers after the
result, or out of order, is dropped (progress must only increase).

Closing the request (the client's cancellation on Streamable HTTP) cancels the upstream request too; it is recorded with
status 499. `notifications/cancelled` from a client is accepted and ignored: on HTTP the closed stream is what counts.

## Change notifications

`subscriptions/listen` (`2026-07-28`) keeps an SSE answer open. The gateway acknowledges `toolsListChanged`,
`promptsListChanged` and `resourcesListChanged` (not `resourceSubscriptions`), and sends
`notifications/{tools,prompts,resources}/list_changed`, with `io.modelcontextprotocol/subscriptionId`, when what the key may
see changes: after every configuration change on any instance (the invalidation bus) and every `Gateway:Mcp:ListenCheckSeconds`
(25 s, also a keep-alive). It compares the key's visible tools (names and approved definitions) and the servers whose
prompts and resources it may use. Changes a server makes to its own prompt or resource lists are not seen, and circuits
opening or closing are not announced. A stream ends with a `complete` result when the key is revoked, expires or is
disabled (checked after a key invalidation and on every check) and when the gateway shuts down. Streams per key are capped
across instances by `Gateway:Mcp:MaxListenStreamsPerKey` (10; Redis `ume:mcp:listen:{key}`), and a stream over the cap gets
`-32000`. Handshake-era clients have no stream: they see changes when they list again.

## Accounting and monitoring

Every `tools/call`, `resources/read` and `prompts/get` that passes authentication is one usage record, whatever its outcome:
`Endpoint = Mcp` (`McpToolCall` for `POST /v1/mcp/tools/call`, which also records `ParentRequestId`), `McpServerId`, `McpMethod`, `McpTool` (the server's name of the tool or prompt; empty for a read, whose
URI is not stored), `RequestedModel = mcp:{slug}/{name}` (`mcp:{slug}` for a read; so reports grouped by model show MCP
calls apart from models; for a name that matched nothing, `mcp:` and the name as sent), cost = the tool's price on success
(nothing on failure), latency, status (200, 4xx for refusals, 502/504 for server failures, 499 when
the client left), outcome, PII categories and action. Arguments and results are never stored or logged.
`ProviderAccountId` is empty, so MCP traffic does not count in provider statistics; the usage summary by provider shows it
as its own row, *MCP*. Budget counters rebuilt from Postgres include MCP costs.

- Metrics: `ume.gateway.mcp.calls` (counter) and `ume.gateway.mcp.call.duration` (ms), tags `server` (slug), `method`,
  `outcome`, for every request sent to a server (lists included); `ume.gateway.mcp.listen_streams` (open
  `subscriptions/listen` streams on the instance); plus the usual `ume.gateway.requests` with `endpoint = Mcp`.
- Spans: `tools/call <tool>` (client) with `mcp.method.name`, `gen_ai.tool.name`, `gen_ai.operation.name = execute_tool`,
  `ume.mcp.server`, `ume.mcp.attempts`.
- Circuit breaker: the provider circuit settings, per MCP server. Transport failures, timeouts and 5xx count; tool errors
  (`isError: true`), JSON-RPC errors and 4xx do not. While open, the server's tools are hidden.

## Using MCP tools with the LLM APIs

A client that runs its own tool loop against `/v1/chat/completions`, `/v1/responses` or `/v1/messages` can use the key's MCP
tools without speaking MCP: it fetches the tools as tool definitions, sends them with its requests, and hands every tool
call the model makes to the gateway, which runs it and answers with the message or block to append to the conversation.
Nothing is added to LLM requests by the gateway; the LLM endpoints work exactly as without MCP.

**`GET /v1/mcp/tools?format=openai|responses|anthropic`** (default `openai`) lists the tools `tools/list` on `/mcp` lists
for the key (same check, see *What a key sees*), by exposed name (`{slug}__{tool}`), with the approved description (the
title when there is none) and input schema unchanged. Listing is not recorded as usage and sends nothing to the servers.

```json
{ "object": "list", "format": "openai", "data": [ /* send as "tools" */ ] }
```

| `format` | Each entry of `data` |
|---|---|
| `openai` (Chat Completions) | `{ "type": "function", "function": { "name", "description", "parameters" } }` |
| `responses` | `{ "type": "function", "name", "description", "parameters", "strict": false }` (Responses treats a missing `strict` as `true`, which most MCP schemas do not satisfy) |
| `anthropic` (Messages) | `{ "name", "description", "input_schema" }` |

**`POST /v1/mcp/tools/call`** takes exactly one tool call as the model produced it, and recognises the format from its shape:

| Format | Request body | Answer (ready to append) |
|---|---|---|
| Chat Completions | an entry of `tool_calls`: `{ "id", "type": "function", "function": { "name", "arguments": "<JSON string>" } }` | `{ "role": "tool", "tool_call_id", "content": "<text>" }` |
| Responses | a `function_call` item: `{ "type": "function_call", "call_id", "name", "arguments": "<JSON string>" }` | `{ "type": "function_call_output", "call_id", "output" }` |
| Anthropic | a `tool_use` block: `{ "type": "tool_use", "id", "name", "input": { … } }` | `{ "type": "tool_result", "tool_use_id", "content": [ … ], "is_error"? }` |

Other members (`status`, an item's `id`, …) are ignored, so items can be passed on as the SDKs return them. Empty
`arguments` mean no arguments. The call goes through every step of a `tools/call` on `/mcp` (*A tool call, step by step*):
authorisation, schema check, `McpCallsPerMinute` (shared with `/mcp`), PII guard on arguments and result, budget, the server,
circuit breaker and one usage record. Only the exposed name is accepted. Resource URIs in the result are namespaced as on
`/mcp`, so they can be read there.

How the result's content blocks become the answer:

| MCP | Chat Completions (`content` string) | Responses (`output`) | Anthropic (`content` blocks) |
|---|---|---|---|
| `text` | the text; several are joined with a blank line | the same, or `input_text` parts when there is an image | `text` blocks |
| `image` (PNG, JPEG, GIF, WebP) | `[image: image/png, 1234 bytes]` (tool messages take only text) | an `input_image` part with a `data:` URL | an `image` block (`source.type: "base64"`) |
| other images, `audio` | `[image: …]` / `[audio: audio/wav, 1234 bytes]` | the same placeholder | the same placeholder |
| `resource_link` | `[resource link: <uri> (<name>)]` | the same | the same |
| embedded `resource` | its `text`, or `[resource: <uri>, <mimeType>, N bytes]` for a blob | the same | the same |
| `structuredContent` | JSON text, only when the result has no `text` block (MCP asks servers that return both to put the same data in a text block) | the same | the same |
| `isError: true` | the text starts with `Error: ` | the same | `"is_error": true` |

Base64 data never becomes text. Refusals and failures are HTTP errors with the gateway's usual error body (`{ "error": {
"message", "type", "code", "request_id", "doc_url" } }`), not tool results:

| HTTP | `code` | When |
|---|---|---|
| 400 | `invalid_request` | The body is not one of the three shapes, or lacks the call's id or name; a malformed `x-ume-parent-request-id`. |
| 400 | `mcp_invalid_arguments` | The arguments are not a JSON object or do not match the input schema. |
| 400 | `pii_blocked` | PII policy: *Block* found personal data in the arguments or the result, or *Reroute to on-prem* and the server is not on-prem. |
| 400 | `provider_rejected` | The server answered with a JSON-RPC error (its message, cut and put through the PII policy). |
| 401, 403 | `invalid_api_key`, … | As on every `/v1` endpoint. |
| 402 | `budget_exceeded` | A budget is used up (priced tools only). |
| 404 | `mcp_tool_not_found` | Unknown tool, or one the key may not use: the same answer (`Unknown tool: '<name>'`) either way. |
| 429 | `rate_limited` | `McpCallsPerMinute` reached; `Retry-After` says when to try again. |
| 502, 504 | `mcp_upstream_failed`, `mcp_result_too_large` | The server could not be reached, failed or timed out (504), or its result is over `MaxResultBytes`. |

A tool that reports its own failure (`isError: true`) is a normal answer (200). To link the tool calls to the LLM request
they belong to, send that request's `x-request-id` in **`x-ume-parent-request-id`**: it is stored as the usage record's
`ParentRequestId` (at most 64 characters: letters, digits, `_`, `-`, `.`, `:`; anything else is refused). The gateway does
not check that it names a request of the same key.

The loop in Python, with the official SDKs (replace the address, key and model):

```python
import httpx
from openai import OpenAI

GATEWAY, KEY = "https://gateway.example.se", "ume-sk-…"
mcp = httpx.Client(base_url=f"{GATEWAY}/v1/mcp", headers={"Authorization": f"Bearer {KEY}"})
openai = OpenAI(base_url=f"{GATEWAY}/v1", api_key=KEY)

def run(call, parent=None):
    answer = mcp.post("/tools/call", json=call, headers={"x-ume-parent-request-id": parent} if parent else {})
    answer.raise_for_status()
    return answer.json()
```

**Chat Completions**

```python
tools = mcp.get("/tools").json()["data"]
messages = [{"role": "user", "content": "Is case KS-1 open?"}]
while True:
    raw = openai.chat.completions.with_raw_response.create(model="ume/chat-standard", messages=messages, tools=tools)
    reply = raw.parse().choices[0].message
    messages.append(reply.model_dump(exclude_none=True))
    if not reply.tool_calls:
        break
    messages += [run(call.model_dump(), raw.headers["x-request-id"]) for call in reply.tool_calls]
print(reply.content)
```

**Responses**

```python
tools = mcp.get("/tools", params={"format": "responses"}).json()["data"]
items = [{"role": "user", "content": "Is case KS-1 open?"}]
while True:
    response = openai.responses.create(model="ume/chat-standard", input=items, tools=tools)
    items += [item.model_dump(exclude_none=True) for item in response.output]
    calls = [item for item in response.output if item.type == "function_call"]
    if not calls:
        break
    items += [run(call.model_dump(exclude_none=True)) for call in calls]
print(response.output_text)
```

**Anthropic Messages**

```python
from anthropic import Anthropic

claude = Anthropic(base_url=GATEWAY, api_key=KEY)
tools = mcp.get("/tools", params={"format": "anthropic"}).json()["data"]
messages = [{"role": "user", "content": "Is case KS-1 open?"}]
while True:
    reply = claude.messages.create(model="ume/chat-advanced", max_tokens=1024, messages=messages, tools=tools)
    messages.append({"role": "assistant", "content": [block.model_dump(exclude_none=True) for block in reply.content]})
    if reply.stop_reason != "tool_use":
        break
    messages.append({"role": "user", "content": [run(block.model_dump(exclude_none=True)) for block in reply.content if block.type == "tool_use"]})
print(reply.content[-1].text)
```

A real client should cap the number of rounds and handle error answers (for example by telling the model the tool failed).

## Client setup

Replace `https://gateway.example.se` with the gateway's address and use a virtual key with MCP grants.

**Claude Code**

```sh
claude mcp add --transport http ume https://gateway.example.se/mcp --header "Authorization: Bearer ume-sk-…"
```

**VS Code** (`.vscode/mcp.json`; the key is asked for once and stored by VS Code)

```json
{
  "inputs": [{ "type": "promptString", "id": "ume-key", "description": "Ume LLM Gateway key", "password": true }],
  "servers": {
    "ume": {
      "type": "http",
      "url": "https://gateway.example.se/mcp",
      "headers": { "Authorization": "Bearer ${input:ume-key}" }
    }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`). Its own remote connectors use OAuth, which the gateway does not offer
yet, so use the `mcp-remote` bridge (needs Node.js):

```json
{
  "mcpServers": {
    "ume": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://gateway.example.se/mcp", "--header", "Authorization:${UME_AUTH}"],
      "env": { "UME_AUTH": "Bearer ume-sk-…" }
    }
  }
}
```

To use a single server with its own tool names, use `https://gateway.example.se/mcp/<slug>` instead.
