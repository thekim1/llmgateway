# MCP gateway

The gateway exposes the municipality's MCP servers ([Model Context Protocol](https://modelcontextprotocol.io)) to MCP
clients such as Claude Code, VS Code and Claude Desktop, under the same virtual keys, budgets, PII policy and residency rules
as the LLM API. See ADR 016 in [adr.md](adr.md) for the decision.

> **Status.** Tools, resources and prompts work end to end (listing, calling and reading, authorisation, PII guard, rate
> limit, budgets, accounting, circuit breaker, metrics), with progress, cancellation and change notifications
> (`subscriptions/listen`). `completion/complete` and resource subscriptions are not supported yet. There is no admin API or
> UI for MCP servers and grants yet: they are rows in `McpServers`, `McpTools` and `VirtualKeyMcpGrants` (the local dev seed
> adds the demo server `demo`, with tools, a resource and a prompt, and grants it all to the dev key).

## Endpoints

| Endpoint | What it serves |
|---|---|
| `POST /mcp` | Every MCP server the key is granted. Tools and prompts are named `{slug}__{name}`, resource URIs are `mcp+ume://{slug}/{uri}` (see *Naming*). |
| `POST /mcp/{slug}` | One server, with the server's own tool and prompt names, resource URIs and list cursors. Recommended when a client only needs one server. |

Both speak Streamable HTTP and are **stateless**: the gateway issues no `Mcp-Session-Id`, keeps no session state, and any
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

## MCP servers

| Column | Meaning |
|---|---|
| `Slug`, `DisplayName`, `Description` | Names. |
| `Url` | The server's Streamable HTTP endpoint. Production allows only `https`, like providers. |
| `Residency`, `IsEnabled`, `OwnerDepartmentId` | As for providers. |
| `AuthType`, `EncryptedCredential` | `None`; `StaticHeaders` (credential: a JSON object of header names and values, e.g. `{"X-Api-Key":"…"}`); or `OAuthClientCredentials` (credential: `{"tokenUrl","clientId","clientSecret","scope"}`; the token is fetched with HTTP Basic client authentication and cached until a minute before it expires). Encrypted with Data Protection like provider credentials. |
| `ForwardHeaders` | Caller headers sent on to the server, by exact name. `Authorization`, `x-api-key`, `api-key`, cookies and MCP/HTTP protocol headers are never forwarded. A forwarded header goes only with that caller's own `tools/call`, never with requests the gateway's client sends on its own. |
| `TimeoutSeconds`, `MaxResultBytes` | Per call (default 30 s and 1 MiB). |

Legacy SSE servers and stdio servers are not supported directly; run them behind a bridge (planned: a hardened container
on the `mcp` network).

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
`Endpoint = Mcp`, `McpServerId`, `McpMethod`, `McpTool` (the server's name of the tool or prompt; empty for a read, whose
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
