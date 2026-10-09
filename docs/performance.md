# Performance

The data plane (`/v1/chat/completions`, `/v1/embeddings`, `/v1/responses`, `/v1/messages`) is on the critical path of every
LLM call, so the gateway is built to add as little latency and CPU as possible. Administration (admin API, UI) is not
optimised the same way. This page describes what the gateway does per request, how to measure it, and the results.

## What a request costs

With warm caches a request makes **no Postgres calls on the request path**. Keys and the routing catalogue (providers,
models, routes, budgets, rules, exchange rate) are cached in memory; usage records are written in batches by a
background writer.

Redis is used for shared counters (rate limits, budgets, circuit state). Before the first byte reaches the client a
request makes **2 Redis round trips** (rate limit pipelined with the circuit-state lookup; budget reservation); after the
response it makes **1** (budget reconciliation and token count, pipelined), off the client's critical path:

| Step | Round trips | Notes |
|---|---|---|
| Rate limit + circuit state | 1 | One Lua script plus one `MGET`, issued together so they are pipelined. |
| Budget reservation | 1 | One Lua script reads, checks and reserves every applicable budget (key lineage, team, förvaltning) and reports counters that need seeding. |
| Circuit reset on success | 0 | Fire-and-forget. |
| Budget reconciliation + token count | 1 | Pipelined, after the response has been written. |
| Usage record | 0 | Queued in memory; written in batches (one `INSERT` per batch, `LastUsedAt` at most once per key and minute). |

Cache behaviour:

- **Expiry never blocks requests.** When a cached key or catalogue snapshot is older than its TTL
  (`Gateway:KeyCacheSeconds`, `Gateway:CatalogCacheSeconds`, default 30 s), it is still served while one background query
  replaces it. Concurrent misses for the same key share one query.
- **Unknown keys are remembered too**, for the same TTL, in a separate cache capped at 10 000 entries (so made-up keys
  cannot push real ones out). A client retrying with a deleted or mistyped key no longer costs a query per request.
  Creating a key publishes the same invalidation as revoking one, so a new key works immediately.
- **Admin changes apply at once.** Revoking a key or changing configuration publishes an invalidation over Redis; the
  next request then loads fresh data before it continues. A load that started before the invalidation is not cached.
- A cold budget counter (first request after start-up or in a new period) is seeded once from the usage table, however
  many requests arrive at the same time.
- If Postgres is unavailable, the last catalogue snapshot keeps being served (previously requests failed once the TTL
  expired); keys that are already cached keep working.

Streaming: each upstream SSE event is parsed only as far as needed (a forward-only scan for the text length; the usage
chunk is parsed fully) and written straight into the response pipe with one flush per event, so tokens reach the client
as soon as they arrive. Non-streaming answers are passed through as the bytes the provider sent: usage is read with a
forward-only scan instead of building and re-serialising a JSON tree, which matters most for embeddings.

## Measuring

`benchmarks/Ume.LlmGateway.Benchmarks` contains [BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks and an
interaction audit. Both need Docker (Postgres and Redis run in Testcontainers) and must run in Release:

```powershell
# All benchmarks (about 20 minutes); results in BenchmarkDotNet.Artifacts\results
dotnet run -c Release --project benchmarks\Ume.LlmGateway.Benchmarks -- --filter *

# One group, e.g. the end-to-end pipeline or the provider/streaming leg
dotnet run -c Release --project benchmarks\Ume.LlmGateway.Benchmarks -- --filter *Pipeline*
dotnet run -c Release --project benchmarks\Ume.LlmGateway.Benchmarks -- --filter *Provider*

# Postgres/Redis calls per request, plus a 15 s concurrent load test
dotnet run -c Release --project benchmarks\Ume.LlmGateway.Benchmarks -- audit
```

| Benchmark | What it measures |
|---|---|
| `PipelineBenchmarks` | A full request through the real gateway (same `Program`, DI and middleware) on Postgres, with in-memory or Redis stores. The provider is an instant in-process stub, so the time is gateway overhead only. The key has rate limits and key/team/förvaltning budgets so every step runs. |
| `ProviderBenchmarks` | The provider leg: serialise, call upstream, read JSON or SSE, write to the client. OpenAI-compatible, Anthropic passthrough and Anthropic translation. |
| `RequestBenchmarks` | Parsing the client body, PII scan, per-attempt rewrite and serialisation (0.5 KB and 64 KB requests). |
| `RoutingBenchmarks` | Model resolution and candidate selection, budget lookup (with 0 and 10 000 rotated keys), key hashing. |
| `audit` | Postgres statements on the request path vs. in the background writer, and Redis commands, per request; then requests/s and tail latency for 64 concurrent streaming clients with 1 s cache TTLs. |

Absolute numbers depend on the machine; compare runs on the same host. In the results below, Postgres and Redis run in
Docker on WSL2, so a Redis round trip costs roughly 0.3–0.5 ms; in production (same network segment) it is usually
below 0.2 ms, and the remaining gateway overhead is correspondingly smaller.

## Results

Measured 2026-10-09 on a 16-core laptop (WSL2, .NET 10.0.12), before and after the optimisation pass described in the
changelog. "Before" is commit `7521b48`.

### End-to-end gateway overhead (`PipelineBenchmarks`)

Time per request with an instant upstream, so this is everything the gateway itself adds.

| Request | Stores | Before | After | Allocated before → after |
|---|---|---:|---:|---:|
| Chat completion | Redis | 4.93 ms | **1.67 ms** (−66 %) | 128 KB → 83 KB |
| Chat stream, 300 chunks | Redis | 5.59 ms | **1.97 ms** (−65 %) | 2.33 MB → 0.76 MB |
| Embeddings, 16 × 1536 | Redis | 7.20 ms | **2.42 ms** (−66 %) | 1.94 MB → 0.70 MB |
| Chat completion | In-memory | 371 µs | **221 µs** (−40 %) | 84 KB → 68 KB |
| Chat stream, 300 chunks | In-memory | 1.46 ms | **0.72 ms** (−51 %) | 2.30 MB → 0.60 MB |
| Embeddings, 16 × 1536 | In-memory | 2.14 ms | **1.02 ms** (−52 %) | 1.93 MB → 0.69 MB |

The Redis rows are dominated by round trips, which cost more in this Docker-on-WSL2 setup than on a production network.

### External calls per request (`audit`, Redis, sequential)

| Scenario | Mean before | Mean after | Postgres on request path | Postgres in background writer |
|---|---:|---:|---|---|
| Chat, warm caches | 5.60 ms | **1.98 ms** | 0 → 0 | 3 → **1** statement per request |
| Chat stream, warm caches | 6.46 ms | **2.51 ms** | 0 → 0 | 3 → **1** |
| Embeddings, warm caches | 6.22 ms | **2.67 ms** | 0 → 0 | 3 → **1** |
| Key and catalogue invalidated before every request | 10.0 ms | 7.58 ms | 9 → 9 (by design: an invalidation reloads synchronously) | 3 → 1 |
| Unknown key, 401 (measured 2026-10-10, before/after the negative key cache) | 0.99 ms | **0.12 ms** | 1 → **0** | 0 → 0 |

Redis round trips: about 10 sequential before (6 before the first byte, 4–5 after; non-streaming answers waited for all
of them), now 2 before the first byte and 1 pipelined after the response. The background writer's figure is for
sequential traffic, where every batch holds one record; under load batches grow and the per-request cost falls further.

Concurrent load (64 streaming clients, cache TTLs shortened to 1 s so expiry happens constantly): **4 594 → 7 897
requests/s**, p50 13.3 → 5.3 ms. p99 was 26 ms before and 35–37 ms after, measured at the higher throughput: the test
saturates the machine, so the tail reflects CPU contention in this setup more than per-request cost.

### Components

| Benchmark | Before | After |
|---|---:|---:|
| Provider leg: chat stream, 300 chunks (OpenAI-compatible) | 746 µs, 2.02 MB | **223 µs, 360 KB** |
| Provider leg: Anthropic `/v1/messages` stream passthrough, 300 chunks | 435 µs, 1.08 MB | **157 µs, 259 KB** |
| Provider leg: Anthropic-translated chat stream, 300 chunks | 799 µs, 1.83 MB | 610 µs, 1.27 MB (translator unchanged, see below) |
| Provider leg: embeddings answer, 16 × 1536 | 1.43 ms, 1.50 MB | **481 µs, 9 KB** |
| Provider leg: chat answer | 5.4 µs, 10.5 KB | **3.6 µs, 6.3 KB** |
| Applicable budgets, 10 000 rotated keys in the system | 963 µs, 1.3 MB | **0.18 µs, 0.5 KB** |
| PII scan, 0.5 KB request | 2.9 µs | **0.5 µs** |
| PII scan, 64 KB request | 639 µs | **102 µs** |

Unchanged and already cheap: body parsing (0.5 µs for 0.5 KB, 47 µs for 64 KB), per-attempt rewrite and serialisation,
route selection (1.1 µs) and key hashing (0.9 µs).

## Comparison with eneo

A presentation version for non-technical readers is in [reports/platform-comparison.html](reports/platform-comparison.html)
(open in a browser, step with the arrow keys; works offline) and [reports/platform-comparison.pdf](reports/platform-comparison.pdf).
Its numbers are copied from the runs below; regenerate it by hand after a new run.

`benchmarks/compare` runs this gateway and [eneo](https://github.com/eneo-ai/eneo) v2.2.1 (production Docker image
built from the tag) against the **same fake LLM**, which answers instantly, so only the platforms are measured. A
third target sends the same requests straight to the fake LLM: that is the floor. Both platforms get the same four
pinned cores for the application and two for Postgres + Redis; the fake LLM and the load generator (k6) have their own
cores. Setup, fairness choices and how to re-run: [benchmarks/compare/README.md](../benchmarks/compare/README.md).

**These are different kinds of product.** The gateway is a stateless proxy that authenticates a key, enforces limits
and budgets, routes and records usage metadata. eneo is a chat application: every message resolves the assistant and
its space, creates a conversation, stores the question and answer, and counts tokens. The numbers show what each
costs on top of the LLM call, not which product is better.

Measured 2026-10-09 (30 s per scenario, 0 failed checks in every scenario; each response verified to contain the fake
LLM's answer). Overhead is the median minus the fake LLM's own median at the same load.

| Chat message, non-streaming | Ume gateway | eneo v2.2.1 |
|---|---:|---:|
| Overhead, 1 client (median) | **1.1 ms** | 51.5 ms |
| Maximum throughput on 4 cores (best of 1/16/64 clients) | **11 680 req/s** | 66 req/s |
| Median / p99 at 64 clients | **3.4 / 30 ms** | 496 ms / 25.3 s (saturated) |
| CPU per message, platform process (16 clients) | **0.37 ms** | 56.5 ms |
| CPU per message, its Postgres + Redis (16 clients) | **0.14 ms** | 11.1 ms |
| Postgres statements per message | **1–2** (background, batched) | 72 |
| Memory of the platform process | 0.2–1.3 GB | 1.8 GB |

| Chat message, streaming | Ume gateway | eneo v2.2.1 |
|---|---:|---:|
| Overhead, 1 client (median, whole stream) | **1.4 ms** | 65.3 ms |
| Time to first byte, 1 client (median) | **1.4 ms** | 50.3 ms |
| Maximum throughput on 4 cores | **10 566 req/s** | 49 req/s |
| CPU per message, platform process (16 clients) | **0.49 ms** | 70.6 ms |
| Postgres statements per message | **1–2** | 74 |

Full table (all loads, p95/p99, TTFB, per-component CPU): `benchmarks/compare/results/report.md` after a run.

Notes for reading the numbers:

- With an LLM in the loop, eneo's ~50–65 ms is small next to a real model's 0.5–30 s, so a single user will not notice
  it. It matters for capacity: at about 57–70 ms CPU per message, eneo needs roughly one core per 14–18 messages per
  second, and queueing sets in early (at 64 concurrent clients the 99th percentile reached 25 s). The gateway's cost
  per message is about 150× lower in CPU and 45× lower in latency.
- CPU per request at one client is inflated for both by idle background work spread over few requests; the 16- and
  64-client rows are the real marginal cost. The gateway's memory grows with load because .NET's server GC keeps a
  larger heap when there is work; it is not a leak.
- The gateway's Redis count (18) includes the operations inside its Lua scripts; that is still 3–4 network round trips.
  eneo uses no Redis on this path.

### Scaling

`benchmarks/compare/scale.sh` measures how both platforms scale with load (1–256 concurrent clients on 4 vCPUs each)
and with resources (1–6 vCPUs, eneo with one worker per vCPU, peak throughput on a fresh stack per size). Streaming
chat, same fake LLM; vCPU means one hardware thread (this machine has 8 cores / 16 threads, CPU sets follow the
physical cores). Measured 2026-10-09.

| Concurrent clients (4 vCPUs) | 1 | 4 | 16 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|
| Ume gateway, msg/s | 581 | 2 157 | 6 393 | 10 668 | 11 470 | 11 417 |
| Ume gateway, median | 1.7 ms | 1.7 ms | 2.4 ms | 5.0 ms | 8.2 ms | 21 ms |
| eneo, msg/s | 15 | 56 | 50 | 44 | 35 | 7 (29 % errors) |
| eneo, median | 65 ms | 68 ms | 247 ms | 0.9 s | 2.9 s | 35 s |

| vCPUs | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Ume gateway, peak msg/s | 4 570 | 9 791 | 11 345 | 11 106 |
| eneo, peak msg/s | 16 | 32 | 56 | 75 |
| eneo, speed-up | 1.0× | 1.97× | 3.49× | 4.67× |

- **eneo** scales close to linearly with workers, but each worker only serves about 13–16 messages/s (64–78 ms CPU per
  message). Beyond about 4 concurrent requests per worker its throughput *falls*: each request holds two or more
  pooled database connections at once, the per-worker SQLAlchemy pool (20 + 10 overflow) starves, and requests fail
  after the 30 s pool timeout with HTTP 500 (`QueuePool limit of size 20 overflow 10 reached`). With Postgres'
  default `max_connections=100`, four workers (up to 120 connections) can also exhaust the database; the scaling runs
  use 500.
- **The gateway** levels off at about 11 000 messages/s from 2 vCPUs, with CPU to spare (3.3 of 6 vCPUs busy at the
  6-vCPU peak; Redis, Postgres and the fake LLM also below capacity). The limit is the usage writer: its queue
  (10 000 records) stayed full for the whole run, so back-pressure caps the request rate at the rate one batched
  insert loop can write to Postgres. Requests slow down instead of failing. Raising it (Postgres `COPY` for batches,
  or more than one writer) is the next step if one instance ever needs more than ~10 000 messages/s; several gateway
  instances each bring their own writer.

### Data protection and resilience (not performance)

Checked in the eneo v2.2.1 source and tested live on 2026-10-09 with the compare stack:

| Safeguard | Ume gateway | eneo v2.2.1 |
|---|---|---|
| Scans message content for personal data | Yes: personnummer and samordningsnummer (Luhn and date checked), e-mail, phone, IBAN | No. Its "redaction" only masks secrets, tokens and e-mail addresses in its own logs (`observability/redaction.py`). Tested: a message with Skatteverket's test personnummer 19121212-1212, an e-mail and a phone number reached the model unchanged |
| Acts on it | Per key: block, redact, keep on-premises, or log only | No |
| Limits which models can be used | Per key: model and provider allow-lists, data residency | Per space: security classification level; a space only accepts models with at least its level (`spaces/space.py`, `validate_model_security_compatibility`). Set by an administrator in advance, not based on content |
| Fallback when a provider fails | Ordered fallback within the allowed residencies; circuit breaker moves failing providers last | No. Tested: provider down gives HTTP 503 "AI service is temporarily unavailable" |
| Stores message content | Never (usage metadata only) | Every question and answer, as chat history; the test personnummer was in `questions` afterwards |

**eneo behind the gateway.** eneo reaches models through OpenAI-compatible provider endpoints, so it can use the gateway
as its provider. Tested with `ENEO_LLM_ENDPOINT=http://ume-gateway:8080/v1`, `ENEO_LLM_MODEL=ume/chat` and a virtual key
with the `Redact` policy: the model received `[PERSONNUMMER]`, `[E-POST]`, `[TELEFON]`, and with the primary provider
unreachable eneo's request succeeded through the fallback provider (`FallbackCount = 1`). eneo still stores the original
text in its own chat history; the gateway only protects what is sent to the model.

The gateway's detection is pattern-based: it does not recognise names, addresses or free-text health information, and
the policy is set per key (off unless configured).

Where eneo's time goes (from reading the v2.2.1 code path for `POST /api/v1/conversations/`, not profiled): about 35
of the 72 statements load the **whole space** for every message (all tenant models, assistants, MCP servers,
capabilities, with a duplicated capability lookup); about 20 create the session and question placeholder (the
question row is re-read right after `INSERT … RETURNING`); the rest are authentication (user, roles, tenant, groups),
governance/skill policy and the final `UPDATE` plus an always-written `logging` row. Each request also builds a
fresh dependency-injection container of ~230 providers, uses three to four transactions over two pooled connections
(holding one open during the LLM call when not streaming), counts tokens 4–8 times with litellm/tiktoken, and goes
through litellm's parameter handling and pydantic serialisation per streamed chunk. Caching the space/assistant
resolution per user would likely remove most of the database work.

## Not done (yet)

- **Anthropic-translated streaming** (`/v1/chat/completions` routed to an Anthropic provider) still builds JSON trees per
  event in `AnthropicStreamTranslator`; writing the OpenAI chunks with `Utf8JsonWriter` would remove most of the
  remaining 1.27 MB per 300-chunk stream. Native `/v1/messages` passthrough is already optimised.
- **Rate limit and budget in one script.** Both are Lua scripts on the same Redis; merging them would save one more round
  trip but couples two independent stores, and the budget estimate depends on routing, which runs in between.
- **Caching decrypted provider credentials.** Data Protection decryption takes a few microseconds per attempt; keeping
  plaintext credentials in memory for the snapshot's lifetime was not judged worth it.
- **Byte-level SSE passthrough** (never decoding events to strings) would save more allocations, but needs a different
  `SseEvent` model shared with the Anthropic translator.
