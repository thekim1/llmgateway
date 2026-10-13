# MCP gateway

The gateway exposes the municipality's MCP servers ([Model Context Protocol](https://modelcontextprotocol.io)) to MCP
clients such as Claude Code, VS Code and Claude Desktop, under the same virtual keys, budgets, PII policy and residency rules
as the LLM API. See ADR 017 in [adr.md](adr.md) for the decision.

> **Status.** Tools, resources and prompts work end to end (listing, calling and reading, authorisation, PII guard, rate
> limit, budgets, accounting, circuit breaker, metrics), with progress, cancellation and change notifications
> (`subscriptions/listen`). Clients of the LLM APIs can list the tools as tool definitions and run the calls a model makes
> (see *Using MCP tools with the LLM APIs*), and `/v1/responses` runs a hosted `mcp` tool itself, with approval (see *Hosted
> MCP tools on `/v1/responses`*, streamed or not), and `/v1/messages` runs Anthropic's MCP connector the same way (see *The
> MCP connector on `/v1/messages`*; not streamed yet).
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
| `POST /v1/responses` with `{"type":"mcp"}` tools | The gateway runs the tool loop itself (see *Hosted MCP tools on `/v1/responses`*). |

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
`Endpoint = Mcp` (`McpToolCall` for `POST /v1/mcp/tools/call` and for calls the gateway runs in a `/v1/responses` or `/v1/messages` tool loop,
which also record `ParentRequestId`), `McpServerId`, `McpMethod`, `McpTool` (the server's name of the tool or prompt; empty for a read, whose
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

## Hosted MCP tools on `/v1/responses`

A `/v1/responses` request can name an MCP server as a hosted tool, in OpenAI's shape. The gateway runs the tool loop itself:
it offers the server's tools to the model as function tools, runs the calls the model makes, sends the conversation again
and repeats until the model answers, a call needs approval, the model calls one of the caller's own functions, or a limit
is reached. It works with any model the key may use on `/v1/responses`. The `mcp` tool is never sent to the provider, and
the gateway never connects to an address the caller sends.

```json
{
  "model": "ume/chat-standard",
  "input": "Är ärende KS-2026-1 avslutat?",
  "tools": [{ "type": "mcp", "server_label": "diarium", "allowed_tools": ["search_cases"],
              "require_approval": { "never": { "tool_names": ["search_cases"] } } }],
  "max_tool_calls": 8
}
```

| Member | Meaning |
|---|---|
| `server_label` | Required: the slug of a server the key may use. Unknown and not allowed get the same 404 `mcp_server_not_found`. |
| `server_url` | Optional; must be exactly the registered URL (400 otherwise). |
| `allowed_tools` | Narrows the tools the key may use: an array of the server's tool names, or `{ "tool_names": [...], "read_only": true }` (`read_only`: only tools annotated `readOnlyHint: true`). |
| `require_approval` | `"always"`, `"never"`, or `{ "always": { "tool_names": [...] }, "never": { "tool_names": [...] } }`. A tool that is not named, or a missing `require_approval`, needs approval (as at OpenAI). |
| `headers`, `authorization`, `connector_id` | Refused (400): the gateway uses the server's own credential; caller headers reach a server only through its `ForwardHeaders`. |

`background: true` and `conversation` cannot be combined with an `mcp` tool yet (400). A function tool of the
caller with the same name as an offered tool (`{slug}__{tool}`) is refused (400). `max_tool_calls` counts the calls run by one HTTP request: a continuation (see *Continuing*) starts a fresh count. A
`function_call` to a name that is neither an offered MCP tool nor one of the caller's own `function` tools is answered to the
model with `Error: Unknown tool: '<name>'` (it counts against `max_tool_calls`, is not shown to the client and is no usage
record). Hosted tools the provider runs itself (for example `web_search`) are sent on unchanged, and their items
(`web_search_call`, …) pass through untouched; they never appear as `function_call`. `tool_choice: { "type": "mcp",
"server_label", "name" }` forces that tool on the first turn; from the second turn on a forced or `"required"` `tool_choice`
becomes `"auto"`, so the model can finish. `max_tool_calls` is enforced by the gateway and not sent on.

**The loop.** Each turn sends the whole conversation again: the input, the model's items exactly as returned (reasoning
included) and a `function_call_output` per call. With `store: false` and a reasoning model (`ParameterProfile`
`OpenAIReasoning`) the gateway adds `include: ["reasoning.encrypted_content"]`, so that reasoning can be replayed, and
leaves it out of the answer unless the caller asked for it. The caller's `previous_response_id`, `store` and `instructions`
are sent unchanged on every turn. Calls run one after the other through the same steps as `tools/call` on `/mcp`
(authorisation, schema check, `McpCallsPerMinute`, PII guard on arguments and result, price and budget, circuit breaker), and
the model gets native image parts. A call that is refused or fails (unknown, rate limited, PII blocked, invalid arguments,
server failure) is reported to the model as `Error: …` and to the client as the `error` of its `mcp_call`; the loop goes on.

**Turns whose calls cannot all run now run none of them**, so a stopped loop never leaves tool results the provider has not
seen, and tool results are never stored:

- a call needs approval (see below);
- the model also calls one of the caller's own `function` tools: the caller's `function_call` is
  returned unchanged for the caller to answer, and the MCP calls of that turn are returned as `mcp_call` with `status:
  "calling"`; they run when the caller continues;
- a limit is reached: the calls are returned as `mcp_call` with `status: "incomplete"` and an `error` whose `message` is
  `"Not run: <reason>."`.

**The answer** is the last turn's response with:

- `output`: one `mcp_list_tools` per `server_label` first (left out when the request has a `previous_response_id`, or its
  input already has the list for that label), then every turn's items, the MCP calls as `mcp_call` (`id` = the model's
  `call_id`, `name` = the server's tool name, `arguments` exactly as the model produced them, `output` as text with image
  placeholders, `error`, `status`, and `approval_request_id` for an approved call) or `mcp_approval_request`. `error` has
  OpenAI's shape: `{ "type": "mcp_tool_execution_error", "content" }` for a result with `isError`, `{ "type": "http_error",
  "code", "message" }` when the server failed, and `{ "type": "mcp_protocol_error", "code", "message" }` otherwise (the
  server's JSON-RPC code, or `-32000` for a call the gateway did not run);
- `usage` summed over the turns; `tools` and `tool_choice` as the caller sent them; `max_tool_calls` as applied;
- `id`: the last turn's provider id, so a later `previous_response_id` works at the provider. When the loop stopped with
  calls still to answer and `store` is not false, a gateway id `resp_ume_…` instead (see *Continuing*);
- `status` and `incomplete_details`: the provider's own when it did not complete (its `max_output_tokens` stays as it is),
  otherwise `incomplete` with one of these **gateway extensions** to OpenAI's reasons:

| `incomplete_details.reason` | When |
|---|---|
| `max_tool_calls` | The next turn's calls would go over `max_tool_calls` (default `Gateway:Mcp:Loop:DefaultMaxToolCalls`, 10; at most `MaxToolCalls`, 50). |
| `max_turns` | `Gateway:Mcp:Loop:MaxTurns` (20) provider turns were sent. |
| `time_limit` | `Gateway:Mcp:Loop:TimeoutSeconds` (300) passed; checked before each turn's calls. |
| `budget_exceeded` | The next turn's budget reservation was refused, or a tool call's price did not fit (the model then gets one more turn, already reserved, to report it). |
| `provider_error` | A turn after the first failed, or a streamed turn broke off: the answer has everything done so far (tools may have changed things), with HTTP 200. |

A request refused before the first turn, or a first turn that fails, is answered as any `/v1/responses` request (4xx/5xx).

**Streaming.** With `stream: true` every turn is streamed from the provider and the client gets one event stream: one
`response.created` and `response.in_progress` (turn 1's response, with the caller's `tools`), the turns' events with
`output_index` counted over the whole answer and `sequence_number` renumbered, and one final `response.completed` or
`response.incomplete` whose `response` is exactly the non-streamed answer (its `id` is the one to continue from, which can
differ from the `id` in `response.created`). The provider's own `response.created`/`completed` per turn are left out.
`mcp_list_tools` items come with `response.mcp_list_tools.in_progress|completed`; an MCP call is shown as an `mcp_call`
whose arguments arrive as `response.mcp_call_arguments.delta|done` (`item_id` = the call's `id`), then
`response.mcp_call.in_progress` and `completed|failed` around the call, then its `output_item.done`. An
`mcp_approval_request` (and a call that may need approval but is not run) is sent whole, added and done, when its turn
ends. Calls to unknown functions send no events. While tools run and the next turn starts, the gateway sends an SSE comment
(`: keep-alive`) every `Gateway:Mcp:Loop:KeepAliveSeconds` (15). The stream starts at turn 1's first event: providers are
tried in turn (fallback) only before that, and calls answered for a continuation run before it. A provider stream that
fails or breaks off later ends the stream with `response.incomplete` and `provider_error`; items of the broken turn that
were already sent are not in the final `output`. A streamed answer has no `x-ume-turns`, `x-ume-cost-sek` or
`x-ume-budget-remaining-sek`, and no `x-ume-pii` for a tool result (the headers are sent before the tools run).

**Approval.** A call needs approval when the request or the key's grant asks for it, whichever is stricter: the grant's
approval policy `Always`, or `NeverForReadOnly` for a tool without `readOnlyHint: true`. Such a turn stops with an
`mcp_approval_request` (`id` `mcpr_…`, `server_label`, `name`, `arguments`) per call that needs it, and the turn's other MCP
calls as `mcp_call` with `status: "calling"`. The gateway keeps the stopped calls, with their arguments, encrypted with Data
Protection in Redis (`ume:mcp:approval:{key}:{id}`, and `ume:mcp:pending:{key}:{id}` for a `resp_ume_` response) for
`Gateway:Mcp:Loop:ApprovalTtlMinutes` (60): the state is bound to the key, used once, and holds no tool result.

**Continuing.** Either way, the answered calls run first (approved ones with exactly the arguments the model produced, denied
ones answered "The user denied this tool call." with the `reason`), then the loop goes on:

- with `previous_response_id: "resp_ume_…"` and `mcp_approval_response` items (and the caller's own `function_call_output`
  items) as input; an approval request left unanswered counts as denied. The continuation goes to the same model
  deployment; when the key may no longer use it, 409 `mcp_continuation_not_found`;
- by sending the history back (needed with `store: false`), including the `mcp_approval_request` items unchanged and an
  `mcp_approval_response` for each (a request sent back without an answer is left out, as if the model had not made the
  call). An `mcp_call` with `status: "calling"` is run; other `mcp_call` items become the call and
  its output for the model; `mcp_list_tools` items are left out.

| HTTP | `code` | When |
|---|---|---|
| 404 | `mcp_continuation_not_found` | The `resp_ume_` id is unknown, expired, of another key, or was continued already. |
| 404 | `mcp_approval_not_found` | An approval request is unknown, expired, of another key, or was answered already (also 409 when two copies of a request race). |
| 400 | `mcp_approval_mismatch` | A resent `mcp_approval_request` differs from the one the gateway issued (tool or arguments changed). |

An example with approval (Python, OpenAI SDK, `store` on):

```python
from openai import OpenAI

client = OpenAI(base_url="https://gateway.example.se/v1", api_key="ume-sk-…")
tools = [{"type": "mcp", "server_label": "diarium", "require_approval": {"never": {"tool_names": ["search_cases"]}}}]
response = client.responses.create(model="ume/chat-standard", input="Stäng ärende KS-2026-1.", tools=tools)
while True:
    requests = [item for item in response.output if item.type == "mcp_approval_request"]
    if not requests:
        break
    answers = [{"type": "mcp_approval_response", "approval_request_id": r.id,
                "approve": input(f"Run {r.name}({r.arguments})? [y/n] ") == "y"} for r in requests]
    response = client.responses.create(model="ume/chat-standard", previous_response_id=response.id, input=answers, tools=tools)
print(response.status, response.output_text)
```

**Accounting.** The first turn is the request itself (its `x-request-id`, rate limit, PII guard, routing rules, fallback,
budget). Every later turn is its own usage record (`Endpoint = Responses`, a new request id, `ParentRequestId` = the first
turn's id), on the first turn's deployment without routing or fallback, with its own budget reservation; it counts towards
the key's tokens per minute but not its requests per minute. Every tool call is a usage record with `Endpoint =
McpToolCall` and the same parent. The first turn of a continuation records the original request as its parent. The body is
PII-scanned on the first turn only: later turns add model output and tool results, which the MCP core has already put
through the key's policy; with *Reroute to on-prem*, a tool result with personal data is withheld from a model that does
not run on-prem (the model gets `Error: Result withheld …`). A client that leaves cancels the turn or call in flight
(accounted 499) and no further turn starts.

Response headers: `x-ume-provider`, `x-ume-model`, `x-ume-residency`, `x-ume-fallbacks` and `x-ume-rule` of the first turn;
`x-ume-cost-sek` for all turns and tool calls; `x-ume-budget-remaining-sek`; `x-ume-pii` (the strongest action of the
request and its tool calls); `x-ume-turns` (provider turns).

## The MCP connector on `/v1/messages`

A `/v1/messages` request can use Anthropic's MCP connector shape (beta `mcp-client-2025-11-20`; the deprecated
`mcp-client-2025-04-04` `tool_configuration` is accepted too). The gateway runs the loop itself, with the rules of the
`/v1/responses` loop above (turns, fallback on turn 1 only, budget per turn, PII, accounting, limits), so it works with any
model the key may use on `/v1/messages`. `mcp_servers`, the `mcp_toolset` tools and the `anthropic-beta` header are never
sent to the provider: the servers' tools go upstream as ordinary tools (`{slug}__{tool}`), and the model's `tool_use` for
them comes back to the client as an `mcp_tool_use` block followed by its `mcp_tool_result`.

```json
{
  "model": "ume/chat-advanced",
  "max_tokens": 1024,
  "messages": [{ "role": "user", "content": "Är ärende KS-2026-1 avslutat?" }],
  "mcp_servers": [{ "type": "url", "name": "diarium" }],
  "tools": [{ "type": "mcp_toolset", "mcp_server_name": "diarium",
              "default_config": { "enabled": false }, "configs": { "search_cases": { "enabled": true } } }]
}
```

| Member | Meaning |
|---|---|
| `mcp_servers[].name` | Required: the slug of a server the key may use. Unknown and not allowed get the same 404 `mcp_server_not_found`. |
| `mcp_servers[].url` | Optional; must be exactly the registered URL (400 otherwise). |
| `mcp_servers[].authorization_token` | Refused (400): the gateway uses the server's own credential. |
| `mcp_toolset` | `default_config.enabled` and `configs.{tool}.enabled` narrow the tools the key may use (server tool names); a pinned `tools` list (beta `mcp-client-2026-09-15`) narrows to its names, but the definitions sent are always the approved ones. `cache_control` goes on the server's last tool; `defer_loading` is ignored. A toolset must name a server in `mcp_servers`, and a server may have only one. |

**No approval.** The connector has no approval protocol, so a tool whose effective policy needs approval (the grant's
`ApprovalPolicy` is `Always`, or `NeverForReadOnly` and the tool is not `readOnlyHint: true`) is simply not offered. Use
`/v1/responses` for such tools.

**The answer** has the content blocks of every turn in order (each MCP call as `mcp_tool_use` + `mcp_tool_result`, whose
`content` is text, with images as placeholders as in the `mcp_call` output), the summed `usage` and the last turn's
`stop_reason`. A `tool_use` for one of the caller's own tools ends the loop (`stop_reason: "tool_use"`); MCP calls of that
turn have run and are shown. A call to a name nobody offered is answered to the model as an unknown tool and not shown.
When a gateway limit stops the loop (`max_tool_calls`, default 10, no request field; `max_turns`; `time_limit`;
`budget_exceeded`; `provider_error` after turn 1), the answer is HTTP 200 with `stop_reason: "pause_turn"` (what Anthropic
returns when its own server-tool loop pauses: send the content back to continue) and the header `x-ume-mcp-stop` with the
reason; calls that were not run have an `mcp_tool_result` with `is_error: true` and `Not run: <reason>.`. A turn that also
calls one of the caller's tools keeps `tool_use`, and a provider's own stop (`max_tokens`, …) is kept. Headers are those of the `/v1/responses` loop plus `x-ume-mcp-stop`.

**History.** Earlier assistant messages with `mcp_tool_use`/`mcp_tool_result` blocks are sent upstream as `tool_use` in the
assistant message and `tool_result` in a user message after it (merged into the next user message when one follows);
`mcp_tool_listing` blocks are left out. Their server must be in the request's `mcp_servers`, and every `mcp_tool_use` needs
its `mcp_tool_result` (400 otherwise). The connector blocks are only translated when the request has `mcp_servers` or an
`mcp_toolset`.

**Not yet:** `stream: true` with the connector is refused (400); the gateway does not emit `mcp_tool_listing` blocks.

## Known limitations and later work

Decisions that shape the feature, and what is not done. None of it is a bug; each item is a choice or a deferral.

**Design decisions**

- Proxy first: tools reach a model only when a request asks for them, with the providers' own shapes (Responses `mcp` tool,
  Anthropic connector); requests without MCP take the unchanged LLM path.
- Upstreams are remote Streamable HTTP only (no legacy SSE, no subprocesses in the gateway); the gateway is stateless towards
  clients, with no `Mcp-Session-Id`, and change notifications only on `subscriptions/listen`.
- The gateway handles Streamable HTTP POST itself instead of the SDK's `MapMcp`, and passes `tools/call`, `resources/read` and
  `prompts/get` results through as raw JSON, so unknown block types and vendor fields survive. Arguments are checked with a
  built-in subset of JSON Schema (`McpSchemaValidator`; JsonSchema.Net is under a paid licence for commercial use, and the
  upstream validates fully anyway).
- Removed tools are deleted, not marked; a tool that comes back needs approval (and a price) again. Grants are not part of the
  configuration export (key ids differ). Unknown tool names in a grant are kept and reported.
- Loop state of a stopped `/v1/responses` loop holds arguments only, encrypted and bound to the key; tool results are never
  stored. After `provider_error` the provider never got the last outputs, so that response cannot be continued with
  `previous_response_id`; send the history back instead.

**Not supported yet**

- Inbound OAuth 2.1 (clients use a virtual key as a static header; Claude Desktop needs `mcp-remote`), per-user upstream
  credentials and token exchange. Planned for v2 in [261013_llmgateway_mcp_auth.md](../261013_llmgateway_mcp_auth.md).
- `completion/complete`, resource subscriptions (`resources/updated`), noticing changes a server makes to its own prompt and
  resource lists, announcing circuit changes, and syncing on an upstream `notifications/tools/list_changed` (the periodic and
  on-demand syncs cover it).
- Upstream sampling, elicitation and roots; progressive discovery (`search_tools`), code execution, virtual tool bundles,
  routing-rule variables for MCP (`mcp_servers`, `mcp_tool_count`) and output guardrails beyond PII (prompt injection).
- A per-server price (only per-tool `priceSek`), the `McpServerAuthFailed` event for gateway calls (the sync raises it; calls
  only log upstream 401/403 and answer `-32003`), a `server` filter on `GET /v1/mcp/tools`, the Anthropic error body shape on
  `/v1/mcp/tools/call` (errors use the OpenAI-style body), and `ParentRequestId` in the admin API's usage views.
- The private-network SSRF rule is checked only when a URL is saved (forbidden addresses are refused on every connection).
- A chat-completions tool loop (`ume_mcp` extension) is not built; clients use `/v1/mcp/tools` or `/v1/responses`.

**Tool results and media.** Images in PNG, JPEG, GIF and WebP are native image parts where the model-facing format takes
them (Responses function output, Anthropic tool results, so also in the `/v1/responses` and `/v1/messages` loops). Everything
else is text: Chat Completions tool messages take no images, audio and other image types become `[audio: …]`/`[image: …]`
placeholders in every format, and what the client sees in `mcp_call.output` and `mcp_tool_result.content` is text only (the
model got the native image). Replaying such an item sends the text. Audio is not passed natively anywhere, because none of the
three tool-result shapes carries it.

**`/v1/responses` loop and streaming**

- A streamed answer starts at turn 1's first event, so calls answered for a continuation run before the stream starts and no
  keep-alive covers them.
- Items of a streamed turn that breaks off are sent but are not in the final `output`.
- A streamed answer has no `x-ume-turns`, cost or budget headers, and no tool-PII header (headers go out before the tools run).
- `background: true` and `conversation` cannot be combined with an `mcp` tool.
- `max_tool_calls` counts per HTTP request; the time limit is not enforced inside a single call (the server's own timeout
  bounds it).

**`/v1/messages` connector**

- `stream: true` with `mcp_servers` is refused with 400; streaming is later work.
- `mcp_tool_listing` blocks are not emitted (and are dropped from history).
- The tool-call limit is fixed (`Gateway:Mcp:Loop:DefaultMaxToolCalls`, 10) because the connector has no request field for it.
  When a limit is reached the answer is `stop_reason: "pause_turn"` with `x-ume-mcp-stop` naming the reason.
- No approval: tools that need approval are not offered (use `/v1/responses`).

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
