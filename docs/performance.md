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
