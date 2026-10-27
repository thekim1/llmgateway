# Performance

The data plane (`/v1/chat/completions`, `/v1/embeddings`, `/v1/responses`, `/v1/messages`) is on the critical path of every
LLM call, so the gateway is built to add as little latency and CPU as possible. Administration (admin API, UI) is not
optimised the same way. This page describes what the gateway does per request, how to measure it, and the results.

## What a request costs

With warm caches a request makes **no Postgres calls on the request path**. Keys and the routing catalogue (providers,
models, routes, budgets, rules) are cached in memory; usage records are written in batches by a
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
- Trusted proxies come with the catalogue snapshot and a key's allowed networks are parsed once when the key is cached,
  so client address restrictions add no external call (see *Components* below for their cost).
- Usage records, budget alerts and `LastUsedAt` are written in one transaction; a batch retried after a lost commit
  answer skips the records already stored by request id, so it cannot write them twice.

Logging and telemetry: successful requests log their "Request … status … in … ms" line at Debug (refused, failed and
cancelled ones at Information), because one formatted line per request cost a noticeable share of peak throughput; the
same facts are in the usage record and the metrics. *Settings > Request logging* turns the line on for every request
for a while; the switch is read from the cached catalogue snapshot. Provider time and time to first token are two or
three timestamp reads per request. The OpenTelemetry GenAI metrics are off by default (`Gateway:GenAiMetrics`): they
add four histogram recordings per request, and `ume.gateway.*` already covers tokens, cost and latency.

Streaming: each upstream SSE event is parsed only as far as needed (a forward-only scan for the text length; the usage
chunk is parsed fully, and the `"usage":null` that OpenAI and Azure put in every chunk does not count as one) and written straight into the response pipe with one flush per event, so tokens reach the client
as soon as they arrive. Non-streaming answers are passed through as the bytes the provider sent: usage is read with a
forward-only scan instead of building and re-serialising a JSON tree, which matters most for embeddings. A chat
completion routed to an Anthropic provider is translated with `Utf8JsonReader` for the text and tool-argument deltas and
a reused `Utf8JsonWriter` for the OpenAI chunks; only the few other events of a stream build JSON trees.

Off the request path:

- `GET /api/usage/metrics` (the Usage dashboard) aggregates in one scan per query (`GROUPING SETS` gives the buckets
  and the period's percentiles together) plus one for the per-model series. Its cost grows with the number of records
  in the period (the percentile sorts), so long periods on a busy gateway take seconds; a department's view uses the
  `(DepartmentId, Timestamp)` index.
- Data retention removes old records nightly in the admin API, in batches of 5 000 rows, each its own short
  transaction, so it never holds locks the gateway's usage writer waits on.

## Measuring

`benchmarks/Ume.LlmGateway.Benchmarks` contains [BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks and an
interaction audit. Both need Docker (Postgres and Redis run in Testcontainers) and must run in Release:

```powershell
# All benchmarks (about 10 minutes); results in BenchmarkDotNet.Artifacts\results
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

Throughput, scaling and streaming latency against a fake LLM in containers with pinned cores are measured with
`benchmarks/compare` (`PLATFORMS="fake ume"` leaves out eneo); see its [README](../benchmarks/compare/README.md). For a
new baseline, run everything in one session on an otherwise idle machine:

```bash
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- --filter '*'   # ~10 min
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- audit         # ~1 min
cd benchmarks/compare
PLATFORMS="fake ume" ./run.sh       # ~8 min
PLATFORMS="fake ume" ./scale.sh     # ~16 min
PLATFORMS="fake ume" ./latency.sh   # ~6 min
```

Check the *Harness check* table at the end of each report: if the fake LLM, Postgres or Redis is close to its limit,
the run measured the harness, not the gateway.

## Results (baseline 2026-10-27)

Every figure in this section comes from one session on one machine, with one harness set-up, so they can be compared
with each other. Every performance figure in the README, on the website, in the platform comparison slides and in the
other docs is taken from here; earlier runs used other harness set-ups and are no longer quoted.

**Set-up.** Commit `8222977` (main after #33), .NET 10.0.13, Release. 8-core / 16-thread laptop, WSL2, Docker.
BenchmarkDotNet and `audit` run the gateway in-process against Postgres 17 and Redis 7.4 in Testcontainers (Redis
without snapshots). `benchmarks/compare` (`run.sh`, `scale.sh`, `latency.sh`, gateway and fake LLM only) runs the
gateway image with pinned hardware threads: gateway 8–11 (4 vCPUs), Postgres 12–13, Redis 14–15 (snapshots off), fake
LLM 0–3 (telemetry off), load generator 4–7. See
[benchmarks/compare/README.md](../benchmarks/compare/README.md#what-is-compared). Raw results:
`benchmarks/compare/results/2026-10-27/` on the machine that ran them.

**The harness was not the limit.** At the gateway's peak (17 888 streamed requests/s) the fake LLM kept 1.6 of its 4
threads busy, Redis 0.46 of its one command thread and Postgres 0.27 of 2, while the gateway used 3.6 of 4. Sent
straight to the fake LLM, the same k6 set-up reaches about 55 000 streamed and 75 000 non-streamed requests/s.

### Added latency and throughput (`benchmarks/compare/run.sh`)

Overhead is the gateway's median minus the fake LLM's own median at the same load. 30 s per scenario, 0 failed checks.

| | 1 client | 16 clients | 64 clients |
|---|---:|---:|---:|
| Non-streaming: requests/s | 747 | 8 634 | **16 805** |
| Non-streaming: overhead (median) | **0.97 ms** | 1.55 ms | 2.72 ms |
| Non-streaming: median / p99 | 1.27 / 1.61 ms | 1.69 / 3.60 ms | 3.29 / 13.8 ms |
| Streaming: requests/s | 606 | 6 449 | **12 618** |
| Streaming: overhead (median, whole stream) | **1.27 ms** | 2.05 ms | 3.68 ms |
| Streaming: time to first byte (median) | **1.28 ms** | 1.82 ms | 3.59 ms |
| Gateway CPU per request, non-streaming / streaming | 2.3 / 2.7 ms¹ | 0.36 / 0.49 ms | **0.20 / 0.27 ms** |
| Postgres + Redis CPU per request, non-streaming | 0.59 ms¹ | 0.11 ms | 0.06 ms |
| Postgres statements per request (background writer) | 4¹ | 0.6 | 0.2 |
| Redis commands per request (incl. inside Lua scripts) | 21 | 21 | 21 |

¹ With one client, idle background work (cache refresh, timers) is spread over few requests; 16 and 64 clients show the
marginal cost.

### Scaling (`benchmarks/compare/scale.sh`, streaming, 20 s per point)

| Concurrent clients (4 vCPUs) | 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Gateway, msg/s | 607 | 1 283 | 2 356 | 3 814 | 6 179 | 9 508 | 12 612 | 15 489 | **17 888** |
| Gateway, median | 1.59 ms | 1.52 ms | 1.63 ms | 1.88 ms | 2.31 ms | 3.12 ms | 4.61 ms | 7.37 ms | 12.5 ms |
| Gateway, p99 | 2.05 ms | 2.05 ms | 2.73 ms | 6.94 ms | 7.80 ms | 10.9 ms | 17.2 ms | 25.4 ms | 59.1 ms |
| Fake LLM directly, msg/s (harness ceiling) | 2 499 | 4 954 | 10 459 | 24 632 | 42 297 | 52 657 | 55 222 | 54 675 | 53 899 |

| vCPUs (peak, fresh stack each) | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Gateway, peak msg/s | 4 751 | 9 616 | 17 772 | **20 550** |
| Speed-up (efficiency) | 1.00× | 2.02× (101 %) | 3.74× (94 %) | 4.33× (72 %) |
| Gateway CPU per message | 0.21 ms | 0.20 ms | 0.21 ms | 0.25 ms |

The gateway scales close to linearly up to 4 vCPUs and costs about 0.2 ms of CPU per streamed message; there were no
errors at any load. From 4 to 6 vCPUs it still gains 16 %, but CPU per message rises (0.25 ms), so each extra vCPU buys
less: on this machine two of the six threads share a physical core with the fake LLM and Postgres, and the median at
the peak grows to 15 ms (p99 85 ms, 384 clients).

The 6-vCPU point borrows one thread from the fake LLM (left with 3) and one from Postgres (left with 1). To check that
this does not limit the gateway, the 4-vCPU peak was measured on that smaller harness too: 16 915, 17 494 and 17 422
msg/s, against 17 772, 16 963 and 17 888 on the full one, the same within run-to-run noise (about ±3 % for a single
20 s sample). At the 6-vCPU peak the fake LLM used 1.9 of 3 threads, Postgres 0.34 of 1 and Redis 0.59 of 1.

### Streaming with real-provider timing (`benchmarks/compare/latency.sh`)

The fake LLM waits 400 ms for the first token and sends 200 words at 25 ms ±30 % each (about 5.6 s per answer), so the
gateway sits in a realistic stream. Measured with the stream probe, 30 s per load:

| Clients | Streams/s | TTFT p50 direct → through gateway | Gap between events p99 | Whole stream p50 | Errors |
|---:|---:|---:|---:|---:|---:|
| 1 | 0.2 | 364 → 403 ms | 36.2 ms (36.3 direct) | 5.60 s (5.58 direct) | 0 |
| 16 | 2.8 | 412 → 397 ms | 36.4 ms (36.2) | 5.63 s (5.65) | 0 |
| 64 | 11.2 | 396 → 411 ms | 36.4 ms (36.4) | 5.62 s (5.62) | 0 |
| 256 | 44.7 | 412 → 427 ms | 36.5 ms (36.5) | 5.62 s (5.61) | 0 |

The ±30 % jitter on the first token is larger than anything the gateway adds, so the differences go both ways (−16 to
+39 ms with one sample stream at 1 client); the pace of the stream and its total length are unchanged. No gap of 250 ms
or more at any load.

### A small host (`benchmarks/compare/sizing/run.sh`)

How much hardware a quiet installation needs. Gateway limited to **0.5 vCPU and 512 MB**, Postgres to 1 vCPU and 1 GB
(on a disk volume), Redis to 0.25 vCPU and 256 MB. The key has PII policy *Allow* and content guard *Record* (rules
only, no guard model; `CheckAnswers` on). Real-provider timing as above. Mixed traffic at a fixed arrival rate: 80 %
streamed, 25 % of messages with a personnummer, email address and phone number, 5 % with a prompt injection, 10 % long
conversations (12 turns). 55 000 messages, 0 failed checks.

| | Idle | 10 msg/s | 50 msg/s | 100 msg/s |
|---|---:|---:|---:|---:|
| Gateway CPU, cores (average / highest sample) | 0.00 | 0.17 / 0.35 | 0.39 / 0.50 | 0.46 / 0.50 |
| Gateway memory | 96 MB | 144–161 MB | 181–197 MB | 201–236 MB |
| Postgres CPU / memory | 0.01 / 56 MB | 0.02 / 53 MB | 0.03 / 57 MB | 0.04 / 65 MB |
| Redis CPU / memory | 0.00 / 4 MB | 0.01 / 4 MB | 0.02 / 4 MB | 0.02 / 4 MB |
| Whole message p50 / p95 / p99 | | | 5.57 / 5.96 / 6.74 s | 5.58 / 6.02 / 6.78 s |

Straight to the fake LLM at 50/s the same mix takes 5.56 / 5.92 / 6.75 s, so the gateway added nothing measurable, also
at 100/s, where it used its whole 0.5 vCPU and was throttled in about a quarter of the CPU periods. A realistic stream is
about 200 events, so a message costs about 6.5–7 ms of gateway CPU here (59 % of it kernel time writing the events),
much more than the short answers in the scaling runs. About 560 streams were open at once at 100/s.

A usage record takes **563 bytes** in Postgres including indexes (34 000 rows). Recommended sizes based on this run are
in [Production setup](production-setup.md#sizing).

### Per-request work (`audit`, in-process, Redis, sequential, 500 requests per scenario)

| Scenario | Mean | p99 | Postgres on request path | Postgres in background | Redis commands |
|---|---:|---:|---:|---:|---:|
| Chat, warm caches | 1.95 ms | 2.37 ms | 0 | 1 | 9 |
| Chat stream, warm caches | 2.49 ms | 3.43 ms | 0 | 1 | 9 |
| Embeddings, warm caches | 2.55 ms | 3.52 ms | 0 | 1 | 9 |
| Key and catalogue invalidated before every request | 7.63 ms | 9.26 ms | 9 (by design) | 1 | 9 |
| Unknown key, 401 | 0.12 ms | 0.26 ms | 0 | 0 | 0 |

Load test (64 streaming clients, cache TTLs 1 s so expiry happens constantly, gateway and harness on the same unpinned
cores): 11 364 requests/s, p50 5.1 ms, p99 13.8 ms.

### Components (BenchmarkDotNet)

End to end through the real gateway (`PipelineBenchmarks`, instant in-process upstream):

| Request | In-memory stores | Redis | Allocated (Redis) |
|---|---:|---:|---:|
| Chat completion | 196 µs | 1.65 ms | 60 KB |
| Chat stream, 300 chunks | 707 µs | 1.95 ms | 579 KB |
| Embeddings, 16 × 1536 | 961 µs | 2.33 ms | 674 KB |

The Redis rows are mostly round trips, which cost more in Docker on WSL2 (0.3–0.5 ms each) than on a production network.

| Benchmark | Time | Allocated |
|---|---:|---:|
| Provider leg: chat answer | 3.3 µs | 7.3 KB |
| Provider leg: chat stream, 300 chunks (OpenAI-compatible) | 231 µs | 360 KB |
| Provider leg: chat stream, 300 chunks, `"usage":null` per chunk | 262 µs | 433 KB |
| Provider leg: Anthropic `/v1/messages` passthrough, 300 chunks | 161 µs | 259 KB |
| Provider leg: Anthropic-translated chat stream, 300 chunks | 259 µs | 379 KB |
| Provider leg: embeddings answer, 16 × 1536 | 455 µs | 9.3 KB |
| Parse body, 0.5 KB / 64 KB | 0.58 / 47.8 µs | 1.0 / 70 KB |
| Rewrite + serialise one attempt, 0.5 KB / 64 KB | 1.08 / 95.0 µs | 1.5 / 67 KB |
| PII scan, 0.5 KB / 64 KB | 0.51 / 104 µs | 0.8 / 126 KB |
| Resolve model + select candidates | 1.3 µs | 3.9 KB |
| Applicable budgets (0 or 10 000 rotated keys in the system) | 0.18 µs | 0.5 KB |
| Hash presented key (HMAC-SHA256) | 0.91 µs | 0.3 KB |
| Resolve client address, no trusted proxies / through a proxy (2 hops) | 0.4 / 47 ns | 0 / 72 B |
| Key address allow-list: none / 5 networks, last matching | 0.1 / 6.2 ns | 0 B |

## Comparison with eneo

A presentation version for non-technical readers is in [reports/platform-comparison.html](reports/platform-comparison.html)
(open in a browser, step with the arrow keys; works offline) and [reports/platform-comparison.pdf](reports/platform-comparison.pdf).
Its numbers are from the eneo v2.2.3 run below (2026-10-27); the data-protection slides were tested on v2.2.1, whose
backports to v2.2.3 do not touch them. Regenerate it by hand after a new run (the PDF is printed from the HTML).

`benchmarks/compare` runs this gateway and [eneo](https://github.com/eneo-ai/eneo) (production Docker image built
from the release tag) against the **same fake LLM**, which answers instantly, so only the platforms are measured. A
third target sends the same requests straight to the fake LLM: that is the floor. Both platforms get the same pinned
cores for the application, Postgres and Redis; the fake LLM and the load generator (k6) have their own cores. Setup, fairness choices and how to re-run: [benchmarks/compare/README.md](../benchmarks/compare/README.md).

**These are different kinds of product.** The gateway is a stateless proxy that authenticates a key, enforces limits
and budgets, routes and records usage metadata. eneo is a chat application: every message resolves the assistant and
its space, creates a conversation, stores the question and answer, and counts tokens. The numbers show what each
costs on top of the LLM call, not which product is better.

**What an eneo message is.** One `POST /api/v1/conversations/` that starts a new conversation with an assistant
without knowledge, attachments or tools, using the fake LLM as its model; the same Swedish question as the gateway gets,
streamed or not. eneo can be called in two ways, and they cost different amounts:

- **As a signed-in user** (`Authorization: Bearer` JWT, the user's personal assistant): what eneo's web app does.
- **With a service API key** (`X-API-Key: sk_…`, scoped to an assistant in a shared space): what an external
  application does, and the counterpart of a gateway virtual key. Each message then also checks the key's guardrails and
  scope, its rate limit in Redis and writes an *API key used* audit event (through eneo's background worker). This is
  the fair comparison, and the one used below.

Exact set-up of both (assistant, key settings, what is stored):
[benchmarks/compare/README.md](../benchmarks/compare/README.md#the-eneo-message).

### eneo v2.2.3 (2026-10-27)

eneo's latest release, through a service API key, measured on the same harness and the same day as the gateway's
baseline (*Results (baseline 2026-10-27)* above), so the gateway figures here are those of the baseline. 4 vCPUs each
(eneo: 4 gunicorn workers plus its background worker, whose CPU is counted), Postgres 2 threads, Redis 2, fake LLM 4,
k6 4. `ENEO_AUTH=apikey PLATFORMS=eneo` with `run.sh`, `scale.sh` and `latency.sh`; every response was checked for the
fake LLM's answer. Raw results: `benchmarks/compare/results/2026-10-27-combined/`.

| Chat message, 4 vCPUs | Ume gateway | eneo v2.2.3 |
|---|---:|---:|
| Added latency, 1 client, non-streaming (median) | **0.97 ms** | 54.3 ms |
| Added latency, 1 client, streaming, whole stream (median) | **1.27 ms** | 67.8 ms |
| Time to first byte, 1 client, streaming (median) | **1.28 ms** | 53.0 ms |
| Peak throughput, streaming | **17 888 msg/s** (256 clients) | 51 msg/s (8 clients) |
| Peak throughput, non-streaming (1/16/64 clients) | **16 805 msg/s** (64 clients) | 56 msg/s (16 clients) |
| Median / p99 at 64 clients, streaming | **4.6 / 17 ms** | 1.0 s / 13.8 s |
| CPU per message, platform (16 clients, streaming) | **0.49 ms** | 79.2 ms |
| CPU per message, its Postgres + Redis (16 clients, streaming) | **0.14 ms** | 6.4 ms |
| Postgres statements per message | **0.7** (background, batched) | 78 |
| Redis commands per message (incl. inside Lua scripts) | 21 | 31 |
| Memory, platform process | 0.4–1.7 GB | 2.2 GB |

Load (streaming, 4 vCPUs, 20 s per point):

| Concurrent clients | 1 | 4 | 8 | 16 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Gateway, msg/s | 607 | 2 356 | 3 814 | 6 179 | 12 612 | 15 489 | **17 888** |
| Gateway, median | 1.6 ms | 1.6 ms | 1.9 ms | 2.3 ms | 4.6 ms | 7.4 ms | 12.5 ms |
| eneo, msg/s | 14 | 48 | **49** | 45 | 36 | 12 (6 % errors) | 8 (53 % errors) |
| eneo, median | 69 ms | 73 ms | 136 ms | 273 ms | 992 ms | 1.5 s | 31.8 s |

Resources (peak over several client counts, fresh stack per size; 6 vCPUs borrow one thread each from the fake LLM and
Postgres, see the baseline):

| vCPUs | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Gateway, peak msg/s | 4 751 | 9 616 | 17 772 | **20 550** |
| eneo, peak msg/s | 13 | 26 | 51 | 60 |
| eneo, speed-up (efficiency) | 1.0× | 1.96× (98 %) | 3.80× (95 %) | 4.50× (75 %) |
| eneo, CPU per message (incl. worker) | 73 ms | 76 ms | 78 ms | 82 ms |

With real-provider timing (400 ms to the first token, 200 words at 25 ms ±30 %, `latency.sh`, stream probe through
the API key):

| Clients | Time to first token p50 / p95: direct | Gateway | eneo | eneo whole stream p95 | eneo errors |
|---:|---:|---:|---:|---:|---:|
| 1 | 364 / 485 ms | 403 / 506 ms | 440 / 695 ms | 5.9 s | 0 |
| 16 | 412 / 516 ms | 397 / 514 ms | 561 ms / 2.3 s | 7.5 s | 0 |
| 64 | 396 / 512 ms | 411 / 519 ms | 1.1 s / 11.2 s | 16.5 s | 0 |
| 256 | 412 / 519 ms | 427 / 610 ms | 33.0 s / 33.5 s | 38.8 s | 137 |

- **Like for like**, the gateway on the same four vCPUs serves about 350× eneo's streaming peak and 300× its
  non-streaming peak, at about 160× less application CPU per message (0.49 against 79 ms at 16 clients).
- **Single samples of eneo vary by about ±10 %**, so its 4-vCPU peak is 49 msg/s in the load sweep and 51 in the
  resources sweep; each figure is used in its own context.
- **Why eneo fails under load.** It scales close to linearly with workers, but each worker serves only about 13
  messages/s (73–82 ms CPU per message), so it needs roughly one core per 13 messages per second. Beyond a few
  concurrent requests per worker its throughput *falls*: each request holds two or more pooled database connections at
  once, the per-worker SQLAlchemy pool (20 + 10 overflow) starves, and requests fail with HTTP 500 after the 30 s pool
  timeout, which is the 32 s median at 256 clients. The compare stack sets Postgres' `max_connections` to 500 so the
  database itself is not the limit.
- **The gateway slows down instead of failing.** At 256 clients requests queue a little longer (median 12.5 ms) and
  none fail.
- With one client, both platforms' CPU per message includes idle background work spread over few messages; the
  16-client figures are the marginal cost. The gateway's memory grows with load because .NET's server GC keeps a larger
  heap when there is work; it is not a leak.
- k6 keeps one connection per client open. gunicorn's workers share one listening socket and each accepts a burst of
  new connections at once, so with many workers in one container a few of them can end up with every client.
  `NO_REUSE=true` makes k6 open a new connection per message to check this; scaling eneo is better done with several
  containers of a few workers each than one container with many.
- With a real model, eneo's added time is small for one user (about 75 ms to the first token) but grows with load:
  at 16 users the 95th percentile to the first token is 2.3 s, at 64 users 11 s, and at 256 users streams wait about
  33 s and 137 of them failed. The gateway's figures stay within the fake LLM's own jitter at every load.

Where eneo's time goes (from reading the code path of `POST /api/v1/conversations/`, not profiled): about half of its
statements load the **whole space** for every message (all tenant models, assistants, MCP servers, capabilities, with a
duplicated capability lookup); a large share create the session and question placeholder (the question row is re-read
right after `INSERT … RETURNING`); the rest are authentication (the API key, user, roles, tenant), governance/skill
policy and the final `UPDATE` plus an always-written `logging` row. Each request also builds a fresh
dependency-injection container of about 230 providers, uses three to four transactions over two pooled connections
(holding one open during the LLM call when not streaming), counts tokens several times with litellm/tiktoken, and goes
through litellm's parameter handling and pydantic serialisation per streamed chunk. Caching the space/assistant
resolution per user would likely remove most of the database work.

### Data protection and resilience (not performance)

Checked in the eneo v2.2.1 source and tested live with the compare stack. The three backports in v2.2.3 (CORS allowlist
for secret-key calls, website loading, JIT e-mail verification) do not change any of it.

| Safeguard | Ume gateway | eneo |
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

## Not done (yet)

- **Rate limit and budget in one script.** Both are Lua scripts on the same Redis; merging them would save one more round
  trip but couples two independent stores, and the budget estimate depends on routing, which runs in between.
- **A covering index for budget seeding** (`INCLUDE ("CostUsd")` on the key/team/department + timestamp indexes) would
  let a cold budget counter be seeded with an index-only scan, but makes every `COPY` insert slower and the indexes
  larger. Seeding happens once per budget and period, inserts for every request, so it was left out.
- **More than one usage writer per instance.** Not needed at the peaks above, where the gateway's own CPU is the limit
  and Postgres is mostly idle; several gateway instances each bring their own writer.
- **Byte-level SSE passthrough** (never decoding events to strings) would save more allocations, but needs a different
  `SseEvent` model shared with the Anthropic translator.
