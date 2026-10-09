# Performance improvement plan (handoff)

Work list for the next round of data-plane performance work, written so it can be picked up in a new session without
the history. Each item has the evidence that motivates it, where to look, a suggested approach, and how to prove it
worked. Background and all measurements: [performance.md](performance.md). Measured 2026-10-09 on an AMD Ryzen 7
9800X3D (8 cores / 16 threads), Docker on WSL2; "vCPU" means one hardware thread.

## Where we stand

| | Value | Source |
|---|---|---|
| Gateway overhead per streamed message, 1 client | 1.4 ms (median 1.7 ms incl. fake LLM) | `scale.sh`, load sweep |
| Peak throughput, 1 / 2 / 4 / 6 vCPUs | 4 570 / 9 791 / 11 345 / 11 106 msg/s | `scale.sh`, resource sweep |
| CPU per message at those peaks | 0.23 / 0.20 / 0.26 / 0.31 ms | same |
| Redis round trips per request | 2 before the first byte, 1 after | `audit` |
| Postgres on the request path | 0 statements (warm caches); about 1 per message in the background writer | `audit`, `run.sh` |
| Memory of the gateway process under load | 1.2–1.3 GB | `run.sh` (64 clients) |
| Errors under overload (256 clients, 4 vCPUs) | 0 % (median 21 ms) | `scale.sh` |

## How to measure (always before and after)

All need Docker and must run in Release. Use the same machine for before/after; numbers are not comparable across hosts.

```bash
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- --filter '*Pipeline*'   # end-to-end, in-process
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- --filter '*Provider*'   # streaming/JSON leg
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- audit                  # DB/Redis calls per request
PLATFORMS="ume" benchmarks/compare/scale.sh                                                   # load + vCPU scaling in containers
```

Rebuild the gateway image first for the container tests:
`dotnet publish src/Ume.LlmGateway.Gateway -c Release /t:PublishContainer -p:ContainerImageTag=compare`.
Usage-writer queue depth under load can be read from `GET /health/operations` (HMAC-signed; see
`OperationsSignature`, the compare stack's pepper is in `benchmarks/compare/lib.sh`).

## Work items, in priority order

### 1. Raise the usage-writer ceiling (per-instance throughput limit)

**Evidence.** Throughput stops at about 11 000 msg/s from 4 vCPUs upward while the gateway uses only 3.3 of 6 vCPUs and
Redis, Postgres and the fake LLM are all below capacity. Sampling `/health/operations` at 192 clients showed the usage
queue at 10 000 / 10 000 for the whole run, draining as soon as the load stopped. One batched insert loop
(`UsageWriter.WriteBatchAsync`, EF `AddRange` + `SaveChangesAsync`, up to 500 records per batch) cannot write faster,
so back-pressure in `EnqueueAsync` limits every request.

**Where.** `src/Ume.LlmGateway.Gateway/Pipeline/UsageWriter.cs`.

**Approach.**
- Write batches with Npgsql binary `COPY` (`NpgsqlConnection.BeginBinaryImportAsync`) into `UsageRecords` instead of EF
  inserts; this is typically an order of magnitude faster and avoids `RETURNING "Id"` per row. Keep idempotency on
  retry: either generate the key client-side (switch the identity `long Id` to a client-generated value, which needs
  a migration) or COPY into a temp table and `INSERT … SELECT … ON CONFLICT DO NOTHING` on `RequestId`.
- Optionally allow N parallel writer loops (configurable, default 1–2) reading from the same channel.
- Keep the existing guarantees: bounded queue with back-pressure (never drop billing data), drain on shutdown,
  `FlushAsync`/`TrackPending` semantics, alerts and `LastUsedAt` handling.

**Done when.** At 6 vCPUs and 192 clients the queue stays well below capacity; peak throughput at 4–6 vCPUs is no
longer flat (target at least 1.5× today's 11 000 msg/s, or a different, identified limit); `HotPathTests` and the
gateway/integration test suites pass; a test covers a retried batch (no duplicate rows).

### 2. Find the next limit and the rising CPU per message

**Evidence.** CPU per message grows with vCPUs (0.20 ms at 2, 0.26 at 4, 0.31 at 6) and 1 vCPU behaves worse at high
concurrency (4 570 msg/s at 64 clients, about 1 950 at 128). This points at contention (locks, thread pool, a single
Redis multiplexer connection) rather than work per request. Re-measure after item 1, because the full queue distorts
this picture.

**Where.** Whole pipeline; suspects: `StackExchange.Redis` multiplexer (one connection for all commands),
`KeyAuthenticator`/`GatewayCatalog` caches, thread-pool settings, GC mode.

**Approach.** Profile under load in the compare stack: `dotnet-counters` (thread-pool queue length, lock contention,
GC pause time, allocation rate) and a `dotnet-trace` CPU sample at 6 vCPUs, 192 clients. Fix what the profile shows;
candidates are a small pool of Redis multiplexers, removing remaining per-request allocations, and GC settings for
small containers.

**Done when.** CPU per message at 6 vCPUs is within about 10 % of the 2-vCPU figure, or the remaining cause is
documented in `performance.md`.

### 3. Rate limit and budget in one Redis round trip

**Evidence.** Two Redis round trips remain before the first byte: the rate-limit script (pipelined with the
circuit-state lookup) and the budget reservation script. In production each round trip is roughly 0.1–0.5 ms.

**Where.** `RedisRateLimiter`, `RedisSpendLedger` (`src/Ume.LlmGateway.Infrastructure/Stores/RedisStores.cs`),
`GatewayRequestHandler` steps 4–8, `BudgetService.ReserveAsync`.

**Approach.** The budget estimate depends on the routing result, which runs between the two. Options: reserve with an
upper-bound estimate computed from the requested model's most expensive target at rate-limit time (when no routing
rule is configured, which is the common case), falling back to today's two-step path when rules apply. Keep the
semantics: a rate-limited request must not reserve budget.

**Done when.** `audit` shows one Redis round trip before the first byte on the common path; budget and rate-limit
tests pass, including rules that change the model.

### 4. Memory footprint under load

**Evidence.** The gateway process grows to 1.2–1.3 GB under 64 concurrent clients (server GC keeps large heaps on many
cores). Not a leak, but it sets the container memory limit and the cost per instance.

**Approach.** Measure with `DOTNET_GCHeapAffinitizeMask`/`DOTNET_GCHeapCount`, DATAS (`DOTNET_GCDynamicAdaptationMode=1`)
and `DOTNET_GCConserveMemory`, against throughput and p99. Pick a default for `deploy/compose.prod.yaml` and document it.

**Done when.** Steady-state memory at 64 clients is at most half of today's with less than 5 % throughput loss, or the
trade-off is documented.

### 5. Anthropic-translated streaming

**Evidence.** `/v1/chat/completions` routed to an Anthropic provider still builds JSON trees per event: about 610 µs and
1.27 MB per 300-chunk stream, versus 157 µs and 259 KB for native passthrough.

**Where.** `AnthropicStreamTranslator` in `src/Ume.LlmGateway.Infrastructure/Providers/AnthropicTranslator.cs`.

**Approach.** Read events with `Utf8JsonReader` (as `ProviderJson` does) and write the OpenAI chunks with
`Utf8JsonWriter` into a pooled buffer. Keep the existing translation tests green and add a byte-for-byte comparison
against the current output for a recorded stream.

**Done when.** `ProviderBenchmarks` "Anthropic translated" allocates at most half of today's and the translation tests
pass.

### 6. Verify horizontal scaling

**Evidence.** Not measured yet. Each instance has its own usage writer and caches and shares Redis and Postgres, so
several instances should add up until Redis or Postgres saturates.

**Approach.** Extend `benchmarks/compare` with 2 and 3 gateway replicas behind a small load balancer (nginx or HAProxy
container), same total vCPUs as one larger instance, and record throughput, p99 and Redis/Postgres CPU.

**Done when.** `performance.md` has a table for 1/2/3 instances and names the shared component that limits it.

### 7. Guard against regressions

**Approach.** Add a short CI job (or a documented pre-release step) that runs `--filter '*Pipeline*'` and the `audit`
and fails when allocations per request grow by more than 20 % or the request path gains a Postgres statement or a
Redis round trip. BenchmarkDotNet timing in shared CI is noisy, so gate on allocations and call counts, not time.

**Done when.** The job exists and is described in `docs/runbook.md` or `HANDOVER.md`.

### Lower priority

- **Byte-level SSE passthrough** (never decoding upstream events to strings): saves further allocations on the main
  streaming path; needs a byte-based `SseEvent` shared with the translator.
- **Cache decrypted provider credentials** per catalogue snapshot: a few microseconds per attempt; a security trade-off
  (plaintext lifetime), so only with a review.

## Starting the next session

Suggested opening prompt:

> Read `docs/performance-improvement-plan.md` and `docs/performance.md`. Start with item 1 (usage-writer ceiling).
> Measure the baseline with `PLATFORMS="ume" benchmarks/compare/scale.sh` and the `audit`, implement the change with
> tests, re-measure, and update `docs/performance.md`, `CHANGELOG.md` and this plan with the results.

Keep the conventions in `HANDOVER.md` (analyzers as errors, `[LoggerMessage]` logging, tests with xUnit v3 + Shouldly)
and update the README and getting-started docs when behaviour changes.
