# Performance

The data plane (`/v1/chat/completions`, `/v1/embeddings`, `/v1/responses`, `/v1/messages`) is on the critical path of every
LLM call, so the gateway is built to add as little latency and CPU as possible. Administration (admin API, UI) is not
optimised the same way. This page describes what the gateway does per request, how to measure it, and the results.

## What a request costs

With warm caches a request makes **no Postgres calls on the request path**. Keys and the routing catalogue (providers,
models, routes, budgets, rules) are cached in memory; usage records are written in batches by a
background writer.

Redis is used for shared counters (rate limits, budgets, circuit state). Before the first byte reaches the client a
request makes **1 Redis round trip** (rate limit and budget reservation in one Lua script, pipelined with the
circuit-state lookup); after the response it makes **1** (budget reconciliation and token count, pipelined), off the
client's critical path. When a routing rule can apply to the key, the reservation waits for routing (the rule may change
the model, and with it the estimate): 2 round trips before the first byte.

| Step | Round trips | Notes |
|---|---|---|
| Rate limit + budget reservation + circuit state | 1 | One Lua script checks the rate limit and, only when it admits the request, reads, checks and reserves every applicable budget (key lineage, team, förvaltning) and reports counters that need seeding; one `MGET` for circuit state, issued with it so they are pipelined. With a routing rule for the key, the reservation is its own script after routing (+1). |
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
./sizing/run.sh                     # ~10 min
```

Check the *Harness check* table at the end of each report: if the fake LLM, Postgres or Redis is close to its limit,
the run measured the harness, not the gateway.

## Results (baseline 2026-11-02)

Every figure in this section comes from one session on one machine, with one harness set-up, so they can be compared
with each other. Every performance figure in the README, on the website, in the platform comparison slides and in the
other docs is taken from here; earlier runs used other harness set-ups or older code and are no longer quoted.

**Set-up.** Commit `30676fc` (main after #55, with Redis command tracing off, #56), .NET 10.0.14, Release. 8-core /
16-thread laptop, WSL2, Docker. BenchmarkDotNet and `audit` run the gateway in-process against Postgres 17 and Redis 7.4
in Testcontainers (Redis without snapshots). `benchmarks/compare` (`run.sh`, `scale.sh`, `latency.sh`, gateway and fake
LLM only) runs the gateway image with pinned hardware threads: gateway 8–11 (4 vCPUs), Postgres 12–13, Redis 14–15
(snapshots off), fake LLM 0–3 (telemetry off), load generator 4–7. See
[benchmarks/compare/README.md](../benchmarks/compare/README.md#what-is-compared). Raw results:
`benchmarks/compare/results/2026-11-02/` on the machine that ran them.

**Against the previous baseline (2026-10-27, commit `8222977`)** the same machine and harness now measure 17–27 %
more throughput at every size, about 15 % less CPU per message, a third less added latency for one client and half the p99
at 64 clients. Most of it comes from Redis command tracing being off (it kept every traced request in memory for 10 s, see
*Memory and the garbage collector*); the single Redis round trip before the first byte (#55) mostly shortens latency.

**The harness was not the limit.** At the gateway's peak (21 097 streamed requests/s) the fake LLM kept 2.1 of its 4
threads busy, Redis 0.56 of its one command thread and Postgres 0.31 of 2, while the gateway used 3.7 of 4. Sent
straight to the fake LLM, the same k6 set-up reaches about 54 000 streamed and 76 000 non-streamed requests/s.

### Added latency and throughput (`benchmarks/compare/run.sh`)

Overhead is the gateway's median minus the fake LLM's own median at the same load. 30 s per scenario, 0 failed checks.

| | 1 client | 16 clients | 64 clients |
|---|---:|---:|---:|
| Non-streaming: requests/s | 1 011 | 10 737 | **21 283** |
| Non-streaming: overhead (median) | **0.61 ms** | 1.23 ms | 2.21 ms |
| Non-streaming: median / p99 | 0.90 / 1.39 ms | 1.36 / 2.99 ms | 2.77 / 6.85 ms |
| Streaming: requests/s | 760 | 7 536 | **14 762** |
| Streaming: overhead (median, whole stream) | **0.90 ms** | 1.74 ms | 3.24 ms |
| Streaming: time to first byte (median) | **0.92 ms** | 1.47 ms | 2.98 ms |
| Gateway CPU per request, non-streaming / streaming | 1.8 / 2.3 ms¹ | 0.29 / 0.41 ms | **0.16 / 0.24 ms** |
| Postgres + Redis CPU per request, non-streaming | 0.52 ms¹ | 0.09 ms | 0.05 ms |
| Postgres statements per request (background writer) | 4¹ | 0.5 | 0.2 |
| Redis commands per request (incl. inside Lua scripts) | 21 | 21 | 21 |
| Gateway memory | 124 MB | 136 MB | 158 MB |

¹ With one client, idle background work (cache refresh, timers) is spread over few requests; 16 and 64 clients show the
marginal cost.

### Scaling (`benchmarks/compare/scale.sh`, streaming, 20 s per point)

| Concurrent clients (4 vCPUs) | 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Gateway, msg/s | 754 | 1 513 | 2 689 | 4 671 | 7 558 | 11 335 | 15 039 | 18 072 | **20 868** |
| Gateway, median | 1.22 ms | 1.22 ms | 1.37 ms | 1.59 ms | 2.00 ms | 2.70 ms | 4.07 ms | 6.68 ms | 11.4 ms |
| Gateway, p99 | 1.87 ms | 1.97 ms | 2.25 ms | 2.84 ms | 3.62 ms | 4.84 ms | 7.68 ms | 14.5 ms | 26.8 ms |
| Fake LLM directly, msg/s (harness ceiling) | 2 520 | 5 028 | 10 335 | 25 444 | 41 444 | 49 426 | 52 855 | 53 637 | 53 717 |

| vCPUs (peak, fresh stack each) | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Gateway, peak msg/s | 5 539 | 11 627 | 21 097 | **24 114** |
| Speed-up (efficiency) | 1.00× | 2.10× (105 %) | 3.81× (95 %) | 4.35× (73 %) |
| Gateway CPU per message | 0.18 ms | 0.17 ms | 0.18 ms | 0.22 ms |

The gateway scales close to linearly up to 4 vCPUs and costs about 0.18 ms of CPU per streamed message; there were no
errors at any load. From 4 to 6 vCPUs it still gains 14 %, but CPU per message rises (0.22 ms), so each extra vCPU buys
less, and the median at the peak grows to 13.6 ms (p99 40 ms, 384 clients). That rise is contention inside the gateway,
not the shared cores; see [CPU per message at 6 vCPUs](#cpu-per-message-at-6-vcpus-2026-11-02).

The 6-vCPU point borrows one thread from the fake LLM (left with 3) and one from Postgres (left with 1). To check that
this does not limit the gateway, the 4-vCPU peak was measured on that smaller harness too (2026-10-27): the same within
run-to-run noise (about ±3 % for a single 20 s sample). At the 6-vCPU peak the fake LLM used 2.3 of 3 threads, Postgres
0.40 of 1 and Redis 0.72 of 1.

### Streaming with real-provider timing (`benchmarks/compare/latency.sh`)

The fake LLM waits 400 ms for the first token and sends 200 words at 25 ms ±30 % each (about 5.6 s per answer), so the
gateway sits in a realistic stream. Measured with the stream probe, 30 s per load:

| Clients | Streams/s | TTFT p50 direct → through gateway | Gap between events p99 | Whole stream p50 | Errors |
|---:|---:|---:|---:|---:|---:|
| 1 | 0.2 | 332 → 371 ms | 36.1 ms (35.5 direct) | 5.61 s (5.54 direct) | 0 |
| 16 | 2.8 | 401 → 399 ms | 36.0 ms (35.9) | 5.61 s (5.64) | 0 |
| 64 | 11.2 | 408 → 416 ms | 35.9 ms (36.0) | 5.62 s (5.61) | 0 |
| 256 | 44.7 | 408 → 419 ms | 35.9 ms (36.0) | 5.60 s (5.60) | 0 |

The ±30 % jitter on the first token is larger than anything the gateway adds, so the differences go both ways (−3 to
+39 ms with one sample stream at 1 client); the pace of the stream and its total length are unchanged. No gap of 250 ms
or more at any load.

### A small host (`benchmarks/compare/sizing/run.sh`)

How much hardware a quiet installation needs. Gateway limited to **0.5 vCPU and 512 MB**, Postgres to 1 vCPU and 1 GB
(on a disk volume), Redis to 0.25 vCPU and 256 MB. The key has PII policy *Allow* and content guard *Record* (rules
only, no guard model; `CheckAnswers` on). Real-provider timing as above. Mixed traffic at a fixed arrival rate: 80 %
streamed, 25 % of messages with a personnummer, email address and phone number, 5 % with a prompt injection, 10 % long
conversations (12 turns). 2 minutes per rate, 20 797 messages through the gateway, 0 failed checks.

| | Idle | 10 msg/s | 50 msg/s | 100 msg/s |
|---|---:|---:|---:|---:|
| Gateway CPU, cores (average / highest sample) | 0.00 | 0.16 / 0.29 | 0.36 / 0.41 | 0.49 / 0.54 |
| Gateway memory | 80–88 MB | 96–164 MB | 155–198 MB | 170–225 MB |
| Postgres CPU / memory | 0.01 / 36–39 MB | 0.01 / 42–47 MB | 0.03 / 42–48 MB | 0.05 / 48–57 MB |
| Redis CPU / memory | 0.00 / 3 MB | 0.00 / 4 MB | 0.01 / 4 MB | 0.02 / 4 MB |
| Whole message p50 / p95 / p99 | | 5.56 / 6.29 / 6.74 s | 5.55 / 6.17 / 6.80 s | 5.56 / 6.14 / 6.80 s |

Straight to the fake LLM at 50/s the same mix takes 5.55 / 6.15 / 6.82 s, so the gateway added nothing measurable, also
at 100/s, where it used its whole 0.5 vCPU and was throttled in three quarters of the CPU periods. A realistic stream is
about 200 events, so a message costs about 5–7.5 ms of gateway CPU here (in the 2026-10-27 run, 59 % of it was kernel time writing the
events), much more than the short answers in the scaling runs. About 560 streams were open at once at 100/s.

A usage record takes **554 bytes** in Postgres including indexes (20 797 rows). Recommended sizes based on this run are
in [Production setup](production-setup.md#sizing).

### Per-request work (`audit`, in-process, Redis, sequential, 500 requests per scenario)

| Scenario | Mean | p99 | Postgres on request path | Postgres in background | Redis commands |
|---|---:|---:|---:|---:|---:|
| Chat, warm caches | 1.38 ms | 1.72 ms | 0 | 1 | 8 |
| Chat stream, warm caches | 1.92 ms | 3.00 ms | 0 | 1 | 8 |
| Embeddings, warm caches | 2.38 ms | 3.53 ms | 0 | 1 | 8 |
| Key and catalogue invalidated before every request | 7.23 ms | 8.41 ms | 9 (by design) | 1 | 8 |
| Unknown key, 401 | 0.11 ms | 0.17 ms | 0 | 0 | 0 |

Load test (64 streaming clients, cache TTLs 1 s so expiry happens constantly, gateway and harness on the same unpinned
cores): 14 071 requests/s, p50 4.4 ms, p99 9.4 ms.

### Components (BenchmarkDotNet)

End to end through the real gateway (`PipelineBenchmarks`, instant in-process upstream):

| Request | In-memory stores | Redis | Allocated (Redis) |
|---|---:|---:|---:|
| Chat completion | 197 µs | 1.07 ms | 60 KB |
| Chat stream, 300 chunks | 464 µs | 1.40 ms | 585 KB |
| Embeddings, 16 × 1536 | 1.01 ms | 1.94 ms | 675 KB |

The Redis rows are mostly round trips, which cost more in Docker on WSL2 (0.3–0.5 ms each) than on a production network.

| Benchmark | Time | Allocated |
|---|---:|---:|
| Provider leg: chat answer | 3.9 µs | 7.3 KB |
| Provider leg: chat stream, 300 chunks (OpenAI-compatible) | 223 µs | 360 KB |
| Provider leg: chat stream, 300 chunks, `"usage":null` per chunk | 256 µs | 433 KB |
| Provider leg: Anthropic `/v1/messages` passthrough, 300 chunks | 151 µs | 259 KB |
| Provider leg: Anthropic-translated chat stream, 300 chunks | 243 µs | 379 KB |
| Provider leg: embeddings answer, 16 × 1536 | 459 µs | 9.3 KB |
| Parse body, 0.5 KB / 64 KB | 0.59 / 47.4 µs | 1.0 / 70 KB |
| Rewrite + serialise one attempt, 0.5 KB / 64 KB | 1.02 / 87.2 µs | 1.5 / 67 KB |
| PII scan, 0.5 KB / 64 KB | 0.47 / 103 µs | 0.8 / 126 KB |
| Resolve model + select candidates | 1.3 µs | 3.9 KB |
| Applicable budgets (0 or 10 000 rotated keys in the system) | 0.18 µs | 0.5 KB |
| Hash presented key (HMAC-SHA256) | 0.87 µs | 0.3 KB |
| Resolve client address, no trusted proxies / through a proxy (2 hops) | 0.4 / 50 ns | 0 / 72 B |
| Key address allow-list: none / 5 networks, last matching | 0.1 / 6.2 ns | 0 B |

## Memory and the garbage collector (2026-11-02)

**Redis command tracing kept every traced request in memory.** The 2026-10-27 baseline's 1.0–1.7 GB under load was not the
garbage collector's choice: the OpenTelemetry Redis instrumentation (enabled by Aspire's `AddRedisClient`) records each
command into a profiling session per traced request and turns them into spans only every 10 seconds, so the request's
activity and its commands stay alive until then. Under load that was 427 MB of live objects (3.9 million) at 64 clients;
with a 1 GiB container limit, the limit in `deploy/kubernetes`, the gateway ran out of memory and crashed within a minute
at 64 clients. Redis command tracing is now off unless `Aspire:StackExchange:Redis:DisableTracing` is set to `false`;
requests and provider calls are still traced. The live heap under the same load is now about 12 MB.

`benchmarks/compare/gc.sh`, same harness as the baseline (gateway on 4 vCPUs), commit after #55, 30 s per scenario. At
64 clients:

| | Requests/s (not streamed / streamed) | p99 ms | Peak memory | CPU ms per request (not streamed) |
|---|---|---|---|---|
| Before the fix, Server GC with DATAS (default) | 15 269 / 11 928 | 16.3 / 16.4 | 1.59 GB | 0.224 |
| Before the fix, 1 GiB container limit | crashed (out of memory) | | | |
| Default (Server GC with DATAS) | 19 098 / 13 504 | 7.9 / 9.4 | 156–161 MB | 0.178 |
| DATAS off (one heap per core) | 20 808 / 14 190 | 7.6 / 8.9 | 373–375 MB | 0.167 |
| `GCConserveMemory=5` | 20 743 / 14 401 | 7.0 / 8.8 | 153–165 MB | 0.165 |
| Workstation GC | 20 653 / 14 243 | 6.7 / 8.6 | 165–169 MB | 0.167 |
| Default, 1 GiB limit | 20 971 / 14 435 | 6.9 / 8.5 | 151–161 MB | 0.163 |
| Default, 512 MB limit | 20 970 / 14 349 | 6.8 / 8.8 | 162–182 MB | 0.163 |

- The fix is worth more than any GC setting: a tenth of the memory, and 25–35 % more throughput with half the p99, since
  the collector no longer copies hundreds of megabytes of live objects.
- After the fix the GC settings differ by run-to-run noise (the first configuration of the run, `default`, was a few per
  cent slower than the same settings later on), except DATAS off, which uses twice the memory for no measurable gain.
  The runtime's default stays; no GC setting is needed in `deploy/`.
- 512 MB is ample for this load; the 1 GiB Kubernetes limit leaves room for large uploads (audio, images) in flight.
- The baseline above (2026-11-02) is measured with the fix.

## CPU per message at 6 vCPUs (2026-11-02)

**Not the CPU layout.** `benchmarks/compare/cores.sh` runs the gateway on four layouts, streaming, 32 and 64 clients per
vCPU, 20 s per point, twice (both runs agree within 2 %; the table gives the range):

| Layout | Gateway threads | Clients | Requests/s | Gateway CPU per message |
|---|---|---:|---:|---:|
| `4own`: 2 physical cores of its own (the baseline's 4 vCPUs) | 8–11 | 128 / 256 | 17 702–18 252 / 20 325–21 050 | 0.199–0.203 / 0.178–0.181 ms |
| `4own-1core`: as `4own`, Postgres and Redis on one thread each | 8–11 | 128 / 256 | 17 587–17 812 / 20 479–20 600 | 0.203 / 0.179 ms |
| `6shared`: two threads shared with the fake LLM and Postgres (the baseline's 6 vCPUs) | 3, 8–12 | 192 / 384 | 21 191–22 009 / 22 694–23 832 | 0.230–0.234 / 0.214–0.218 ms |
| `6own`: 3 physical cores of its own | 8–13 | 192 / 384 | 21 193–21 554 / 22 976–23 476 | 0.236–0.237 / 0.220–0.221 ms |

With 3 physical cores of its own the gateway costs as much per message as on the shared layout, and giving Postgres
and Redis less does not change the 4-vCPU figure. CPU per message also falls with more clients per vCPU (0.20 → 0.18 ms
on 4 vCPUs): fuller batches per socket read and write.

**Contention in the gateway.** `benchmarks/compare/profile.sh` measures inside the gateway at the `4own` and `6own`
peaks: the container's user and kernel CPU over 20 s, `dotnet-counters` and a 12 s `dotnet-trace` sample.

| | 4 vCPUs, 256 clients | 6 vCPUs, 384 clients |
|---|---:|---:|
| Requests/s (counters and trace running) | 19 612 | 22 953 |
| Cores busy | 3.73 of 4 | 5.31 of 6 |
| CPU per message | 0.190 ms | 0.231 ms |
| – user mode | 0.135 ms | 0.172 ms |
| – kernel | 0.055 ms | 0.059 ms |
| Allocated per message | 72 KB | 73 KB |
| Time in GC pauses | 4.3 % | 5.4 % |
| Lock contentions/s | 2 654 | 1 884 |
| Thread-pool work items per message | 6.7 | 7.0 |

- The extra 0.04 ms is user mode; the kernel (sockets, syscalls) adds almost nothing.
- Work per message does not grow: allocation, work items and the gateway's own code (authentication, routing, JSON,
  SSE: 1–2 % of the sampled time, the same share on both) stay the same.
- What grows is threads getting in each other's way. The trace (sampled thread time: which managed frame each thread is
  in, about 100 times a second; wall clock, not CPU) shows these shares of managed thread time from 4 to 6 vCPUs:
  waiting for a GC 16.8 → 20.7 %, contended locks (`Monitor.Enter` slow path, which spins before it blocks) 8.0 →
  9.1 %, thread-pool workers waiting for work (they spin before they sleep) 4.4 → 5.7 %, and StackExchange.Redis waking
  its backlog thread 2.8 → 3.8 %.
- The contended locks: 6 % are `HttpHeaders.CloneHeaderInfo` from `HttpClient.PrepareRequestMessage`. The provider
  client sets its `User-Agent` in `DefaultRequestHeaders` (`ProviderHttpClient`), and .NET copies the default headers
  into every request under a lock on the shared header. 5 % are StackExchange.Redis's write lock on its one connection;
  when a writer finds it taken, the command goes to a backlog and a dedicated thread is woken (all of the `SetEvent`
  time). The rest are Kestrel's and System.IO.Pipelines' per-connection locks.
- The GC: allocation per message is unchanged, but more heaps and threads take part in each collection. Pause share
  × cores busy puts the GC at about 0.009 ms per message on 4 vCPUs and 0.014 ms on 6, a small part of the 0.04 ms.

So the remaining cost at 6 vCPUs is contention, mostly in the runtime and libraries: spinning in locks and in idle
thread-pool workers, and Redis write collisions on one connection. Candidates, each to be measured on its own with
`profile.sh` and `cores.sh`: the `User-Agent` per request instead of in the shared default headers; a lower
thread-pool spin limit (`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit`); more than one Redis connection. Results:
`results/cores/report.md`, `results/profile/report.md`.

**`User-Agent` per request (measured 2026-11-02).** The provider client no longer has default headers; the adapters and
the realtime handshake set `User-Agent` on each request. The `CloneHeaderInfo` lock is gone from the trace, but CPU per
message does not change beyond noise (before and after interleaved, two runs each, `cores.sh`):

| Gateway CPU per message | Before | After |
|---|---:|---:|
| 4 vCPUs, 256 clients | 0.180 / 0.180 ms | 0.180 / 0.181 ms |
| 6 vCPUs, 384 clients | 0.222 / 0.221 ms | 0.221 / 0.224 ms |
| 6 vCPUs, 384 clients (`profile.sh`) | 0.226 ms | 0.224 ms |

That lock was about 0.6 % of managed thread time (6 % of the 9 % in contended locks), too little to show.

## Comparison with eneo

A presentation version for non-technical readers is in [reports/platform-comparison.html](reports/platform-comparison.html)
(open in a browser, step with the arrow keys; works offline) and [reports/platform-comparison.pdf](reports/platform-comparison.pdf).
Its numbers are from the eneo v2.2.3 run below (2026-11-02); the data-protection slides were tested on v2.2.1, whose
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

### eneo v2.2.3 (2026-11-02)

eneo's latest release, through a service API key, measured on the same harness and the same day as the gateway's
baseline (*Results (baseline 2026-11-02)* above), so the gateway figures here are those of the baseline. 4 vCPUs each
(eneo: 4 gunicorn workers plus its background worker, whose CPU is counted), Postgres 2 threads, Redis 2, fake LLM 4,
k6 4. `ENEO_AUTH=apikey PLATFORMS=eneo` with `run.sh`, `scale.sh` and `latency.sh`; every response was checked for the
fake LLM's answer. Raw results: `benchmarks/compare/results/2026-11-02/`. eneo's own figures agree with its
previous run (2026-10-27) within run-to-run noise.

| Chat message, 4 vCPUs | Ume gateway | eneo v2.2.3 |
|---|---:|---:|
| Added latency, 1 client, non-streaming (median) | **0.61 ms** | 53.1 ms |
| Added latency, 1 client, streaming, whole stream (median) | **0.90 ms** | 66.4 ms |
| Time to first byte, 1 client, streaming (median) | **0.92 ms** | 51.9 ms |
| Peak throughput, streaming | **20 868 msg/s** (256 clients) | 49 msg/s (8 clients) |
| Peak throughput, non-streaming (1/16/64 clients) | **21 283 msg/s** (64 clients) | 57 msg/s (16 clients) |
| Median / p99 at 64 clients, streaming | **4.1 / 7.7 ms** | 715 ms / 13.5 s |
| CPU per message, platform (16 clients, streaming) | **0.41 ms** | 80.7 ms |
| CPU per message, its Postgres + Redis (16 clients, streaming) | **0.11 ms** | 6.6 ms |
| Postgres statements per message | **0.6** (background, batched) | 78 |
| Redis commands per message (incl. inside Lua scripts) | 21 | 31 |
| Memory, platform process | **124–186 MB** | 2.4 GB |

Load (streaming, 4 vCPUs, 20 s per point):

| Concurrent clients | 1 | 4 | 8 | 16 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Gateway, msg/s | 754 | 2 689 | 4 671 | 7 558 | 15 039 | 18 072 | **20 868** |
| Gateway, median | 1.2 ms | 1.4 ms | 1.6 ms | 2.0 ms | 4.1 ms | 6.7 ms | 11.4 ms |
| eneo, msg/s | 14 | 48 | **49** | 48 | 37 | 18 (3 % errors) | 8 (57 % errors) |
| eneo, median | 67 ms | 72 ms | 132 ms | 273 ms | 715 ms | 1.9 s | 32.2 s |

Resources (peak over several client counts, fresh stack per size; 6 vCPUs borrow one thread each from the fake LLM and
Postgres, see the baseline):

| vCPUs | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Gateway, peak msg/s | 5 539 | 11 627 | 21 097 | **24 114** |
| eneo, peak msg/s | 14 | 27 | 50 | 62 |
| eneo, speed-up (efficiency) | 1.0× | 1.93× (97 %) | 3.63× (91 %) | 4.48× (75 %) |
| eneo, CPU per message (incl. worker) | 73 ms | 76 ms | 79 ms | 84 ms |

With real-provider timing (400 ms to the first token, 200 words at 25 ms ±30 %, `latency.sh`, stream probe through
the API key):

| Clients | Time to first token p50 / p95: direct | Gateway | eneo | eneo whole stream p95 | eneo errors |
|---:|---:|---:|---:|---:|---:|
| 1 | 332 / 583 ms | 371 / 544 ms | 394 / 973 ms | 6.2 s | 0 |
| 16 | 401 / 518 ms | 399 / 511 ms | 550 ms / 2.4 s | 7.6 s | 0 |
| 64 | 408 / 509 ms | 416 / 520 ms | 818 ms / 10.5 s | 15.7 s | 0 |
| 256 | 408 / 521 ms | 419 / 577 ms | 32.7 s / 33.4 s | 38.7 s | 128 |

- **Like for like**, the gateway on the same four vCPUs serves about 430× eneo's streaming peak and 370× its
  non-streaming peak, at about 200× less application CPU per message (0.41 against 81 ms at 16 clients).
- **Single samples of eneo vary by about ±10 %**, so its 4-vCPU peak is 49 msg/s in the load sweep and 50 in the
  resources sweep; each figure is used in its own context.
- **Why eneo fails under load.** It scales close to linearly with workers, but each worker serves only about 13
  messages/s (73–84 ms CPU per message), so it needs roughly one core per 13 messages per second. Beyond a few
  concurrent requests per worker its throughput *falls*: each request holds two or more pooled database connections at
  once, the per-worker SQLAlchemy pool (20 + 10 overflow) starves, and requests fail with HTTP 500 after the 30 s pool
  timeout, which is the 32 s median at 256 clients. The compare stack sets Postgres' `max_connections` to 500 so the
  database itself is not the limit.
- **The gateway slows down instead of failing.** At 256 clients requests queue a little longer (median 11.4 ms) and
  none fail.
- With one client, both platforms' CPU per message includes idle background work spread over few messages; the
  16-client figures are the marginal cost. In the previous run the gateway's memory grew with load (0.4–1.7 GB) because
  Redis command tracing kept every traced request alive for 10 s; it is off by default since 2026-11-02 (see *Memory
  and the garbage collector*).
- k6 keeps one connection per client open. gunicorn's workers share one listening socket and each accepts a burst of
  new connections at once, so with many workers in one container a few of them can end up with every client.
  `NO_REUSE=true` makes k6 open a new connection per message to check this; scaling eneo is better done with several
  containers of a few workers each than one container with many.
- With a real model, eneo's added time is small for one user (about 60 ms to the first token) but grows with load:
  at 16 users the 95th percentile to the first token is 2.4 s, at 64 users 10.5 s, and at 256 users streams wait about
  33 s and 128 of them failed. The gateway's figures stay within the fake LLM's own jitter at every load.

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
