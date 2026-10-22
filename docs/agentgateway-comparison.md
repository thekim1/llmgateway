# Comparison with agentgateway

A feature and design comparison between this gateway and [agentgateway](https://github.com/agentgateway/agentgateway)
(Rust, Apache 2.0, Linux Foundation; `main` as of 2026-10-22), followed by the plan for what we take from it. For
maintainers; not on the website.

## What agentgateway is

A general-purpose proxy (L4/L7, Kubernetes Gateway API controller in Go, xDS or a YAML file for config) that added
AI protocols on top: an LLM gateway, an MCP gateway (tool federation, OpenAPI-to-MCP, OAuth) and an A2A gateway. The
LLM part is one feature of a broader data plane. Configuration is declarative (YAML/CRDs, CEL expressions); there is
no database of organisations, keys or prices. We are the other way round: an LLM gateway for one kind of organisation,
with the organisation (departments, teams, keys, budgets) and its rules in Postgres, run by administrators in a UI.

## Side by side

| Area | agentgateway | Us | Better |
|---|---|---|---|
| Client APIs | OpenAI chat, Responses, embeddings, images, Anthropic Messages + `count_tokens`, Gemini/Vertex native, rerank, OCR, realtime | OpenAI chat, Responses, embeddings, images, audio transcription/translation, realtime + realtime translation, Anthropic Messages | Tie: they have more surfaces (rerank, count_tokens, Gemini native); we have speech to text and metered live audio |
| Providers | OpenAI, Azure, Anthropic, Gemini, Vertex, Bedrock, Copilot, custom | OpenAI, Azure OpenAI, Azure AI Foundry, Anthropic, Ollama, OpenAI-compatible (vLLM) | Them (Bedrock/Vertex/Gemini), not needed for our users today |
| Format translation | Typed passthrough (`rest` keeps unknown fields), golden tests per provider pair, fuzzing | OpenAI <-> Anthropic, unknown fields pass through | Them on test depth (golden files, fuzzing) |
| Load balancing | Power of two choices on a score of EWMA latency, EWMA success rate and in-flight requests; locality buckets; optional session affinity (rendezvous hashing) | Priority groups, weighted random within a group, open circuits moved last | **Them**: we ignore how fast or busy a target is |
| Unhealthy upstreams | Outlier ejection per endpoint: honours the upstream `Retry-After` (429), multiplicative back-off for repeat offenders, gradual recovery score; CEL-defined "unhealthy" | Shared Redis circuit breaker per provider account: 5 transient failures in 60 s open it for 30 s | **Them** on 429 handling (we keep sending to a provider that told us when to come back); **us** on sharing state across instances |
| Retries | Retry policy (attempts, back-off, codes) separate from fallback | Fallback to the next candidate only | Them, marginally; fallback covers most cases |
| Budgets | Per API key, in-memory counters per instance synced to Postgres/SQLite, rolling windows, USD or tokens | Department -> team -> key, atomic Redis reservation before the call and reconciliation after, calendar months, alerts, refunds, rotation keeps spend | **Us**: hierarchy, multi-instance correctness, reservation |
| Rate limits | Local token buckets (requests or LLM tokens) keyed by any CEL expression; remote limits via the Envoy RLS protocol | Shared Redis fixed one-minute windows per key (requests and tokens) | Mixed: their buckets are smoother and keyable by anything; ours hold across instances |
| Token estimate before the call | Real BPE count (o200k, Claude multipliers) when `tokenize: true` (documented as expensive) | Characters / 4 | Them on accuracy, us on cost; ours is only a reservation estimate |
| Cost catalogue | Bundled catalogue of prices, context limits, modalities and capabilities for every provider/model, refreshable from agentgateway.dev; prices per service tier (flex/priority/reserved), cache-write prices | Prices entered per deployment (Azure retail price lookup and discovery help) with effective dates | **Them** on breadth; **us** on effective-dated prices per deployment |
| Prompt caching | Inserts Anthropic/Bedrock cache markers automatically (system, messages, tools, minimum size); bills cache writes at the cache-write rate | Passes client cache markers through; cache **writes are billed at the plain input price** (Anthropic charges 1.25x) | **Them**; ours under-bills cache writes |
| Guardrails | Request and response guards, including streaming: regex (built-in PII recognisers or custom), webhook, OpenAI moderation, Azure Content Safety, Bedrock Guardrails, Google Model Armor; mask or reject | Request-side PII policy per key tuned to Swedish data (personnummer, samordningsnummer, IBAN, phone, email): allow, redact, block or **reroute to on-prem** | Them on breadth (response side, external services); **us** on Swedish detection and residency-aware rerouting |
| Request shaping | Per-route defaults, overrides, CEL transformations, prompt prepend/append, model aliases with wildcards | Parameter profiles per deployment, routing rules (conditions on model, headers, body, key/team, budget use, PII, prompt size; weighted targets, chains), public model names | Tie: their CEL is more general; our routing rules can see budgets and PII and have a dry run and an audit trail |
| Client auth | JWT, OIDC, OAuth, API keys, basic auth, external authz, CEL RBAC | Virtual keys (HMAC), IP allow-lists, rotation with grace period, attachment policy | Them on breadth; SSO for clients is out of our scope |
| Data protection | Logs can include content (configurable) | Metadata only, never content; retention job, PII incidents, masked audit | **Us** |
| Organisation and admin | Config files/CRDs, a read-mostly UI and playground | OIDC BFF admin UI with RBAC per department, reports, CSV per cost centre, Data API, audit log, config import/export | **Us** |
| Telemetry | OpenTelemetry GenAI semantic conventions (`gen_ai.client.token.usage`, `gen_ai.server.request.duration`, time to first token, time per output token, inter-chunk latency) | Own `ume.gateway.*` metrics including overhead, TTFT, stream gaps | Them on interoperability (standard dashboards work); us on gateway-overhead visibility |
| MCP / A2A | Full MCP and A2A gateways | Removed (out of scope) | Them; deliberately not ours |
| Deployment | Kubernetes controller (Gateway API), xDS, single binary | Aspire, Compose with hardening, Kubernetes manifests, zero-downtime updates, backup/restore | Different targets |
| Performance | Rust, no GC; designed as a general proxy | .NET 10, measured 0 Postgres calls and 2 Redis round trips before the first byte, ~11k msg/s per instance (usage writer bound) | Not measured head to head |

### What they do better that matters to us

1. **Rate-limited providers.** When a provider answers 429 with `Retry-After: 20`, agentgateway takes it out of rotation
   for 20 s. We count it as one failure of five, keep sending traffic there (each request pays a wasted round trip
   before falling back), and when five land the whole provider account is closed for a fixed 30 s, including its
   other deployments, which have their own quotas on Azure.
2. **Picking between equal targets.** Same model on two Azure regions or on several vLLM servers at the same
   priority: agentgateway prefers the faster, less busy, more successful one. We split by static weight, so a slow or
   saturated GPU server keeps its share.
3. **Cache-write billing.** Anthropic bills prompt-cache writes at 1.25x the input price (2x for the 1-hour cache).
   We bill them at the input price, so Claude Code-style traffic through `/v1/messages` is under-charged.
4. **Automatic prompt caching** for translated Anthropic requests (an OpenAI-SDK client calling a Claude deployment
   cannot add cache markers itself).
5. **Response-side guardrails.** Their external moderation services (OpenAI, Azure, AWS, Google) are not an option
   for us: scanning must stay on-prem, so the equivalent is in-process rules or a guard model on our own on-prem
   providers.
6. **GenAI semantic-convention metrics**, so off-the-shelf Grafana/Azure Monitor GenAI dashboards work.
7. **A bundled price/limit catalogue** to pre-fill prices for non-Azure models.

### What we do better

Hierarchical, multi-instance-correct budgets with reservation; residency classes and "reroute to on-prem" for PII;
Swedish PII detection; metadata-only storage with retention and audit; an administrator UI with department RBAC,
reports and cost-centre export; routing rules that see budgets and PII, with a dry run; speech to text and metered
live audio; exactly-once accounting of every accepted request.

## Plan

Ordered by value for our users against risk to the hot path.

| # | Item | Status |
|---|---|---|
| 1 | **Honour upstream `Retry-After`.** A 429/503 with `Retry-After` (or `retry-after-ms`) puts that *deployment* on cooldown for that long (capped by `CircuitBreaker:MaxRetryAfter`, default 5 min), shared through Redis. Cooled-down deployments go last, like open circuits. Read in the same `MGET` as circuit state: no extra round trip. | Implemented |
| 2 | **Latency-aware load balancing (opt-in).** `Gateway:LoadBalancing=LeastLoaded` picks the first target within a priority group by power of two choices over a per-instance score of EWMA latency, EWMA success and in-flight requests (agentgateway's formula); the rest keep the weighted order as fallbacks. In memory only: no Redis. Default stays `Weighted` because P2C does not keep weight proportions (a 90/10 canary). | Implemented |
| 3 | **Cache-write pricing.** Separate cache-write tokens in usage and an optional cache-write price per deployment (empty = input price, as today). | Implemented |
| 4 | Automatic Anthropic cache markers for translated requests, and session affinity (`x-ume-session`) for on-prem prefix caches. | Implemented |
| 5 | GenAI semantic-convention metrics next to `ume.gateway.*` (opt-in: `Gateway:GenAiMetrics`), and a USD cost counter. | Implemented |
| 6 | Content guardrails on requests and answers, **on-prem only**: in-process rules plus an optional guard model (Llama Guard, Granite Guardian, Prompt Guard) on an on-prem provider, per key policy, metadata-only incidents. No cloud moderation services. | Planned, needs a scope decision |
| 7 | Anthropic `count_tokens` passthrough, and skipping models whose context window the prompt exceeds. | Implemented |
| 9 | Retry of the last candidate after a passing failure. | Implemented |
| 10 | Sliding-window rate limits and a per-key cap on requests in progress. | Implemented |
| 11 | Request policies per team and key (output cap, system prompt, defaults, overrides). | Implemented |
| 12 | `Utf8JsonWriter` for the Anthropic-translated stream. | Implemented |
| 13 | Usage journal: no usage records lost or requests held up while Postgres is down (Redis, spill on failure). | Implemented |
| 14 | Token quotas (budgets in tokens, optionally per residency), as agentgateway's token-unit budgets. | Implemented |
| 8 | Golden-file tests for the OpenAI <-> Anthropic translation. | Planned |

Not planned: MCP/A2A (out of scope; see *MCP and A2A* below for how it could fit), client SSO (out of scope), Bedrock/Vertex/Gemini (no users), a bundled price
catalogue (prices are contractual for us; the Azure lookup covers the main provider).

## Results of items 1 to 3

`audit 500` (Release, Redis, same machine, back to back; 64 streaming clients for the load line):

| | Redis commands per chat request | Load: req/s | p50 | p99 |
|---|---|---|---|---|
| Before | 9 (one `MGET`) | 10 301 | 5.48 ms | 14.52 ms |
| After, `Weighted` (default) | 9 (one `MGET`) | 11 341 | 5.15 ms | 12.22 ms |
| After, `LeastLoaded` | 9 (one `MGET`) | 11 209 | 5.25 ms | 12.01 ms |

No extra round trip: cooldowns are read in the circuit-state `MGET`. The throughput difference is run-to-run noise, not a
speed-up; the point is that the per-attempt bookkeeping (one dictionary lookup, a few atomics, one small allocation) does
not show.

## MCP and A2A: how agentgateway does it, and whether it fits us

Background: we shipped an MCP gateway in 0.4.0 and removed it in 0.4.1 (`RemoveMcpGateway`) because it was unclear
how it was meant to be used. agentgateway's answer to that question is the useful part.

### Their MCP gateway

- **What it is:** a route whose backend is a list of MCP *targets*. Each target is a remote server (Streamable HTTP or
  SSE), a local command (stdio, for example `npx @modelcontextprotocol/server-everything`) or an **OpenAPI spec turned
  into tools** (each operation becomes a tool; the gateway builds the HTTP call). Several targets are **multiplexed**
  into one virtual server: tools are prefixed `<target>_<tool>`, resource URIs `<target>+<scheme>://…`, list results
  are merged, and `initialize`/`server/discover` negotiate the intersection of protocol versions. A CEL `condition`
  per target can leave a server out for some callers.
- **Sessions:** stateful upstream sessions are kept, but the gateway's `Mcp-Session-Id` is the **encrypted session state
  itself** (which upstream sessions belong to it, AES-encoded). Any instance can decrypt it and resume, so there is no
  shared session store. Stateless mode wraps each request in a generated `initialize`.
- **Who the caller is:** a **person or workload with an OAuth/JWT token**, not an API key. The gateway implements the
  MCP authorization spec as a resource server: it serves `/.well-known/oauth-protected-resource`, validates tokens
  against the IdP's JWKS, and even proxies Entra ID's authorize/token endpoints (stripping the `resource` parameter
  Entra rejects, adding the client secret for confidential app registrations). The token can be passed through to the
  upstream server, so the tool acts **as the user**.
- **What a caller may do:** `mcpAuthorization` CEL rules per tool, prompt and resource
  (`jwt.sub == "anna" && mcp.tool.name == "get-sum"`); denied tools are hidden from `tools/list` too. `mcpGuardrails`
  run CEL or a remote webhook before and after a call.
- **What it records:** traces, metrics and logs per MCP method and tool. **No cost, no budgets, no usage ledger.** A
  tool call is traffic to authorise and observe, not something to bill.
- **How it is used** (their examples): a developer points Claude Code, VS Code or the MCP Inspector at one gateway URL,
  signs in through the IdP, and sees the tools the organisation has put behind it. Adding or removing a server changes
  the gateway config, not every client.

### Their A2A gateway

Thin by design: a route marked `a2a: {}` in front of an ordinary HTTP backend. The gateway classifies each JSON-RPC
call (`message/send`, `tasks/get`, …) for logs, metrics and authorization, inspects the response outcome, and
**rewrites the agent card** (`/.well-known/agent.json`) so its `url` points back at the gateway, so clients never
bypass it. No translation, no state.

### Where our 0.4.0 attempt differed, and why "how is it used" had no answer

| | Our 0.4.0 | agentgateway |
|---|---|---|
| Caller identity | Virtual key (one per application) | The person, via OAuth/JWT from the IdP |
| Per-caller access | Grants on keys, tool approval/quarantine | CEL rules on token claims (user, group) |
| Accounting | Usage records, rate limits and budgets per tool call | Telemetry only |
| Coupled to the LLM API | Hosted `mcp` tools on `/v1/responses`, the connector on `/v1/messages`, `/v1/mcp/tools` | Separate; the client runs its own tool loop |
| Session state | Stateless only | Encrypted state in the session id |
| Upstreams | Remote HTTP | HTTP, SSE, stdio, OpenAPI |

The mismatch is identity. MCP tools act on things (a case file, a ticket, a mailbox), so the meaningful question is
*which person* is calling, and the meaningful control is what *that person* may do. Our model answers "which
application, which budget", which suits paid LLM tokens. For tools it gives an app-level key that every user of a
client shares, a budget for something that costs no money, and usage records that say little. The tokens the model
spends deciding to call a tool are already accounted on the LLM API.

### Would their way fit our platform?

Yes, as a **separate tool gateway next to the LLM API**, not folded into it:

- **Fits as it is:** a stateless HTTP endpoint (their encrypted-session trick is easy in .NET with Data Protection,
  which we already use); HTTP and OpenAPI upstreams; multiplexing with prefixed names; metadata-only audit of each call
  (who, server, tool, outcome, latency, never arguments or results), which matches our data rules; the PII scan of
  tool arguments we already had.
- **Needs something we do not have:** gateway **clients signing in with the municipality's IdP** (OIDC/Entra as an MCP
  resource server). Admin sign-in already uses OIDC, but client SSO is out of scope today; MCP would bring it into scope.
  Access would be granted to AD groups or roles (`groups contains "socialtjansten"`), not to keys.
- **Leave out:** budgets and rate limits per tool call (no money involved), hosted MCP inside `/v1/responses` and
  `/v1/messages` (that coupling made 0.4.0 heavy), and **stdio upstreams** (spawning processes inside the hardened,
  read-only gateway container is a security problem; run such servers as their own containers and connect over HTTP).
- **A2A:** easy to add as a pass-through route with agent-card rewriting and audit, but nothing in the organisation
  speaks A2A today. Wait for a concrete agent.

**Concrete uses that would justify it:** developers using Claude Code or Copilot against internal systems through one
approved, audited URL; existing internal REST APIs (case management, document search) exposed as tools from their
OpenAPI specs without writing an MCP server; an allow-list so only reviewed MCP servers are reachable, with a central
log of who called which tool. If none of these is wanted now, keeping MCP out of scope remains the right call.
