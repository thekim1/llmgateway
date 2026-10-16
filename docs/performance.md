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
| Usage record | 0 | Queued in memory; written in batches of up to 5 000 with one binary `COPY`, budget alerts and `LastUsedAt` (one statement for all keys, each at most once a minute) in the same transaction. |

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
- Provider credentials are decrypted once per catalogue snapshot, not once per request or attempt.
- Budget alert webhooks are sent from their own queue, so a slow receiver never holds up the usage writer.

Streaming: each upstream SSE event is parsed only as far as needed (a forward-only scan for the text length; the usage
chunk is parsed fully, and the `"usage":null` that OpenAI and Azure put in every chunk does not count as one — see
[streaming-findings.md](streaming-findings.md)) and written straight into the response pipe with one flush per event, so tokens reach the client
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
| `ClientAddressBenchmarks` | Resolving the client address behind trusted proxies and checking a key's address allow-list, with and without the features in use. |
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
| Provider leg: chat stream, 300 chunks, OpenAI wire format with `"usage":null` per chunk (measured 2026-10-13) | 672 µs, 1.58 MB | **258 µs, 433 KB** |
| Provider leg: Anthropic `/v1/messages` stream passthrough, 300 chunks | 435 µs, 1.08 MB | **157 µs, 259 KB** |
| Provider leg: Anthropic-translated chat stream, 300 chunks | 799 µs, 1.83 MB | 610 µs, 1.27 MB (translator unchanged, see below) |
| Provider leg: embeddings answer, 16 × 1536 | 1.43 ms, 1.50 MB | **481 µs, 9 KB** |
| Provider leg: chat answer | 5.4 µs, 10.5 KB | **3.6 µs, 6.3 KB** |
| Applicable budgets, 10 000 rotated keys in the system | 963 µs, 1.3 MB | **0.18 µs, 0.5 KB** |
| PII scan, 0.5 KB request | 2.9 µs | **0.5 µs** |
| PII scan, 64 KB request | 639 µs | **102 µs** |

Unchanged and already cheap: body parsing (0.5 µs for 0.5 KB, 47 µs for 64 KB), per-attempt rewrite and serialisation,
route selection (1.1 µs) and key hashing (0.9 µs).

### Release cleanup check (2026-10-10)

The architecture/DRY cleanup before the first release (see the changelog) was checked against commit `1f38195` on the
same machine, run back to back. Same requests, same external calls: 0 Postgres statements on the request path and
9 Redis commands per request in both, and the same 9 statements per cache miss.

| Benchmark | Before | After |
|---|---:|---:|
| `POST /v1/chat/completions`, in-memory stores | 340 μs ± 116, 69.9 KB | **204 μs ± 16**, 68.8 KB |
| `POST /v1/chat/completions`, Redis | 1,725 μs ± 210, 85.7 KB | **1,524 μs ± 31**, 85.2 KB |
| Chat stream (300 chunks), in-memory / Redis | 758 / 2,021 μs | 754 / 1,953 μs |
| Embeddings (16×1536), in-memory / Redis | 1,041 / 2,438 μs | 1,003 / 2,321 μs |
| Provider leg, request parsing, routing | | unchanged (within ±2 %, same allocations) |
| Load test, 64 streaming clients, 4 runs each | 7,214–7,895 req/s (mean 7,530) | 7,112–7,658 req/s (mean 7,435) |

The steadier chat time comes mainly from caching decrypted provider credentials per catalogue snapshot instead of
decrypting them on every attempt. The load-test difference is within run-to-run noise.

### Client address restrictions (2026-10-12)

Key address allow-lists and trusted proxies (see *Client addresses and trusted proxies* in the [runbook](runbook.md)) add
no external call: trusted proxies come with the catalogue snapshot and a key's networks are parsed once when the key is
cached. Same machine, run back to back:

| Benchmark | Time | Allocated |
|---|---:|---:|
| Resolve client address, no trusted proxies (every request when unused) | 0.4 ns | 0 B |
| Resolve client address through a trusted proxy, `X-Forwarded-For` with 2 hops | 51 ns | 72 B |
| Key without address restriction (every request when unused) | 0.1 ns | 0 B |
| Key with 5 allowed networks, the last one matching | 6.2 ns | 0 B |

| `PipelineBenchmarks` | Before | After |
|---|---:|---:|
| `POST /v1/chat/completions`, in-memory / Redis | 216 ± 41 / 1,474 ± 67 μs | 195 ± 7 / 1,498 ± 53 μs |
| Chat stream (300 chunks), in-memory / Redis | 734 ± 245 / 1,869 ± 302 μs | 514 ± 101 / 1,874 ± 263 μs |
| Embeddings (16×1536), in-memory / Redis | 1,008 ± 69 / 2,327 ± 84 μs | 1,010 ± 48 / 2,582 ± 256 μs |

The pipeline differences are within run-to-run noise (the error columns overlap); allocations per request are unchanged
within 0.3 KB.

With data retention added as well (`audit`, three runs each, back to back): warm requests still make 0 Postgres calls and
9 Redis commands. A key + catalogue cache miss makes 10 statements instead of 9 (the catalogue also reads the
`GatewaySettings` row), and seeding a budget counter reads `UsageRetainedFrom` once. Load test, 64 streaming clients
with 1 s cache TTLs: 7,756–8,160 req/s (mean 7,968) against 7,755–8,317 req/s (mean 8,096) before, within run-to-run noise.
The nightly removal runs in the admin API in batches of 5 000 rows, each its own short transaction, so it does not hold
locks the gateway's usage writer waits on.

### Usage dashboard timings (2026-10-13)

The gateway records provider time (`UpstreamMs`) and time to first token (`TimeToFirstTokenMs`) for the Usage dashboard:
two `TimeProvider.GetTimestamp()` reads around each provider attempt and the stream, one more on the first streamed event,
no allocation and no external call. `audit`, same machine, back to back:

| Scenario | Before | After |
|---|---:|---:|
| chat (warm caches), mean / p99 | 2.056 / 2.354 ms | 2.047 / 2.443 ms |
| chat stream (warm caches), mean / p99 | 2.591 / 3.468 ms | 2.525 / 3.674 ms |
| embeddings (warm caches), mean / p99 | 2.794 / 4.078 ms | 2.676 / 3.582 ms |
| Load, 64 streaming clients | 7,272 req/s, p99 41.5 ms | 7,162 req/s, p99 42.0 ms |

Postgres (0 on the request path, 1 in the background) and Redis (9) calls per request are unchanged; the differences are
within run-to-run noise. The migration adds two nullable columns, which Postgres does without rewriting the table.

`GET /api/usage/metrics` (admin API, not the request path) aggregates in one scan per query (`GROUPING SETS` gives the
buckets and the period's percentiles together) plus one for the per-model series. On 2 million records spread over 30
days (Postgres 17, default settings): 24 hours per hour 0.11 s, 7 days 0.63 s, 30 days 1.8 s for a gateway-admin (all
departments); a department's view uses the `(DepartmentId, Timestamp)` index and took 0.02 / 0.13 / 0.40 s. The cost
grows with the number of records in the period (the percentile sorts), so a 90-day view of a very busy gateway takes a
few seconds.

### Usage writer: COPY (2026-10-14)

The usage writer was the ceiling under load (see *Scaling* below; the 2026-10-13 benchmark fell to about 8 500 req/s
when usage rows gained columns). It now writes each batch with one Postgres binary `COPY` instead of EF's
`INSERT … RETURNING` per row through the change tracker, takes up to 5 000 records per batch (was 500), and sets
`LastUsedAt` for all keys of a batch in one `UPDATE … FROM unnest(…)` instead of one statement per key. Records, alerts
and `LastUsedAt` share one transaction; a retried batch skips the records already stored by request id, so a commit
whose answer was lost cannot write them twice.

`benchmarks/compare` (`PLATFORMS=ume VUS="16 64"`, 30 s, same machine, back to back; before = commit `d4c5eaf`):

| Scenario | Before req/s | After req/s | Median before → after | p95 before → after |
|---|---:|---:|---:|---:|
| Non-streaming, 16 clients | 7 087 | 8 206 | 1.66 → 1.58 ms | 3.09 → 2.43 ms |
| Non-streaming, 64 clients | 8 492 | **15 083** (+78 %) | 3.61 → 3.18 ms | 27.5 → 7.57 ms |
| Streaming, 16 clients | 5 807 | 6 176 | 2.29 → 2.15 ms | 3.58 → 3.29 ms |
| Streaming, 64 clients | 7 726 | **10 878** (+41 %) | 5.32 → 4.64 ms | 21.9 → 9.84 ms |

Streaming at 64 clients, per request: gateway CPU 0.368 → 0.314 ms, Postgres + Redis CPU 0.152 → 0.076 ms, Postgres
statements 1.0 → 0.3 (background writer). The gateway's memory at that load rose from 904 to 1 389 MB, from serving
40 % more requests at once (larger batches account for at most a few MB).

`audit 500`: per-request calls are unchanged (0 Postgres statements on the request path, 1 in the background for
sequential traffic, 9 Redis commands); sequential latencies are within noise (chat 1.90 → 1.76 ms mean). Its load
test (64 streaming clients, 1 s cache TTLs) went from 7 876 to **12 412 req/s**, p99 40.4 → 11.5 ms.

### Per-request log line (2026-10-15)

Every request used to log "Request … key … model … status … in … ms" at Information: one formatted message per
request for the console and OpenTelemetry. The same facts are in the usage record and the metrics, so successful
requests now log it at Debug; refused, failed and cancelled requests still log it at Information.

What the line cost, measured first with the unchanged gateway at `GATEWAY_LOG_LEVEL=Information` and `Warning`
(`benchmarks/compare`, `PLATFORMS=ume VUS="16 64"`, 30 s, two runs each, alternating):

| Scenario | Information req/s | Warning req/s | Difference |
|---|---:|---:|---:|
| Non-streaming, 64 clients | 14 966 / 16 062 | 18 167 / 17 883 | +16 % |
| Streaming, 64 clients | 11 747 / 11 816 | 13 064 / 13 091 | +11 % |
| Non-streaming, 16 clients | 7 687 / 8 421 | 9 419 / 8 857 | +13 % |
| Streaming, 16 clients | 6 290 / 6 580 | 6 997 / 7 030 | +9 % |

The change, at the production default (Information), two runs:

| Scenario | Before req/s | After req/s | Gateway CPU per request |
|---|---:|---:|---:|
| Non-streaming, 64 clients | 14 966 / 16 062 | 18 116 / 16 693 (+12 %) | 0.229 / 0.216 → 0.189 / 0.204 ms |
| Streaming, 64 clients | 11 747 / 11 816 | 12 836 / 12 254 (+6 %) | 0.292 / 0.291 → 0.266 / 0.280 ms |
| Non-streaming, 16 clients | 7 687 / 8 421 | 9 264 / 9 047 (+14 %) | |
| Streaming, 16 clients | 6 290 / 6 580 | 7 073 / 6 688 (+7 %) | |

The admin switch *Settings > Request logging* (log every request for 24 hours or without a time limit) is read from the
cached catalogue snapshot: with it off, a request costs one comparison more. Measured with it off, two runs: non-streaming
at 64 clients 18 126 / 17 902 req/s, streaming 12 721 / 13 067, the same as without the switch within run-to-run noise.

Not done: a covering index (`INCLUDE ("CostUsd")`, `CostSek` when this was measured) on the key/team/department + timestamp indexes) would let a cold
budget counter be seeded with an index-only scan. On 5 million records it seeded a yearly department budget in 22 ms
instead of 86 ms (team budgets 2.8 vs 3.5 ms), but made `COPY` inserts 28 % slower and the indexes 55 % larger. Seeding
happens once per budget and period; inserts happen for every request, so it was left out.

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
  insert loop can write to Postgres. Requests slow down instead of failing. Since 2026-10-14 the writer uses `COPY`
  (see *Usage writer: COPY* above), which lifted the 64-client peak from about 8 500 to 15 000 non-streaming requests/s
  on this machine; more than one writer is the next step if that is ever not enough. Several gateway instances each
  bring their own writer.

#### Re-run 2026-10-15 (COPY writer, per-request log line at Debug)

Same script, same machine, gateway only (`PLATFORMS=ume ./scale.sh`); the eneo figures above are unchanged.

| Concurrent clients (4 vCPUs) | 1 | 4 | 16 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|
| Gateway 2026-10-09, msg/s | 581 | 2 157 | 6 393 | 10 668 | 11 470 | 11 417 |
| Gateway 2026-10-15, msg/s | 614 | 2 372 | 6 612 | **13 036** | **15 817** | **17 642** |
| Median 2026-10-09 → 10-15 | 1.71 → 1.52 ms | 1.70 → 1.56 ms | 2.39 → 2.17 ms | 4.97 → 4.37 ms | 8.24 → 7.12 ms | 20.6 → 12.6 ms |
| p99 2026-10-09 → 10-15 | 2.23 → 1.91 ms | 2.97 → 2.41 ms | 7.46 → 7.50 ms | 25.1 → 14.5 ms | 48.3 → 24.0 ms | 61.1 → 55.5 ms |

| vCPUs | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Gateway 2026-10-09, peak msg/s | 4 570 | 9 791 | 11 345 | 11 106 |
| Gateway 2026-10-15, peak msg/s | 4 770 | 10 785 | **17 750** | 18 089 |
| Speed-up over 1 vCPU (efficiency) | 1.00× | 2.26× (113 %) | 3.72× (93 %) | 3.79× (63 %) |
| Gateway CPU per message | 0.21 ms | 0.18 ms | 0.20 ms | 0.26 ms |
| With 4 threads for the fake LLM | | | 17 997 | **20 944** (4.39×, 73 %) |

- With few clients there is little to gain (a request then takes about 1.5 ms of which the gateway's own work is a small part);
  the change shows where the usage writer used to be the ceiling: from 64 clients and from 4 vCPUs up. Postgres statements
  fell from 1.0 to 0.1 per message (the writer now copies about ten records per statement) and Postgres CPU from 0.78
  to 0.3 cores at the peak.
- The gateway now scales close to linearly up to 4 vCPUs (93 % efficiency, was 62 %). Up to 2 vCPUs the efficiency is
  above 100 % because the first thread also carries the runtime's own work.
- At 6 vCPUs the test harness becomes the limit: the fake LLM, on two hardware threads (one physical core), was 83 % busy
  while the gateway used 4.7 of its 6 threads. Given four threads (`FAKE_CPUS=0-1,12-13 DATA_CPUS=14-15`; Postgres and
  Redis use under one thread together) it served 20 944 msg/s with the gateway at 5.1 of 6 threads. The 4-vCPU peak did not
  move (17 997), so there the gateway is the limit.
- Redis: 18 commands per message and about half of one thread at the peak, so it is not close to its single-thread limit yet.

### Data protection and resilience (not performance)

Checked in the eneo v2.2.1 source and tested live on 2026-10-09 with the compare stack:

| Safeguard | Ume gateway | eneo v2.2.1 |
|---|---|---|
| Scans message content for personal data | Yes: personnummer and samordningsnummer (date checked; Luhn for bare 10 digits), e-mail, phone, IBAN | No. Its "redaction" only masks secrets, tokens and e-mail addresses in its own logs (`observability/redaction.py`). Tested: a message with Skatteverket's test personnummer 19121212-1212, an e-mail and a phone number reached the model unchanged |
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
