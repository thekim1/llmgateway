# Performance improvement plan (handoff)

Work list for the next round of data-plane performance work, written so it can be picked up in a new session without
the history. Each item has the evidence that motivates it, where to look, a suggested approach, and how to prove it
worked. All figures are from the baseline of 2026-11-02 in [performance.md](performance.md#results-baseline-2026-11-02)
(8 cores / 16 threads, Docker on WSL2; "vCPU" means one hardware thread). Re-measure the whole baseline in one session
after a change, and replace the figures there and here, so numbers never come from mixed runs.

## Where we stand

| | Value | Source |
|---|---|---|
| Gateway overhead per message, 1 client (median) | 0.61 ms non-streaming, 0.90 ms streaming | `run.sh` |
| Peak throughput, streaming, 1 / 2 / 4 / 6 vCPUs | 5 539 / 11 627 / 21 097 / 24 114 msg/s | `scale.sh`, resource sweep |
| CPU per message at those peaks | 0.18 / 0.17 / 0.18 / 0.22 ms | same |
| What limits the peak | The gateway's own CPU (3.7 of 4 vCPUs busy); fake LLM, Postgres and Redis well below their limits | harness check in the reports |
| Redis round trips per request | 1 before the first byte (2 when a routing rule can apply to the key), 1 after; 8 commands (since #55) | `audit`, `run.sh` |
| Postgres on the request path | 0 statements (warm caches); 0.2–0.7 per message in the background writer under load | `audit`, `run.sh` |
| Memory of the gateway process under load | 124–186 MB at 1–64 clients (was 1.0–1.7 GB before Redis command tracing was turned off, #56) | `run.sh`, `gc.sh` |
| Errors under overload (256 clients, 4 vCPUs) | 0 % (median 11.4 ms) | `scale.sh` |

## How to measure (always before and after)

All need Docker and must run in Release. Use the same machine for before/after; numbers are not comparable across hosts.

```bash
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- --filter '*Pipeline*'   # end-to-end, in-process
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- --filter '*Provider*'   # streaming/JSON leg
dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- audit                  # DB/Redis calls per request
PLATFORMS="fake ume" benchmarks/compare/scale.sh                                              # load + vCPU scaling in containers
```

Rebuild the gateway image first for the container tests:
`dotnet publish src/Ume.LlmGateway.Gateway -c Release /t:PublishContainer -p:ContainerImageTag=compare`.
Usage-writer queue depth under load can be read from `GET /health/operations` (HMAC-signed; see
`OperationsSignature`, the compare stack's pepper is in `benchmarks/compare/lib.sh`).

## Work items, in priority order

### 1. Rate limit and budget in one Redis round trip (done 2026-11-02, #55)

One Lua script checks the rate limit and reserves the budget when no routing rule can apply to the key: 8 Redis commands
per chat request instead of 9, Redis-backed Pipeline benchmark 1.51 → 1.08 ms per chat request. The rest of this item
is kept as it was planned.


**Evidence.** Two Redis round trips remain before the first byte: the rate-limit script (pipelined with the
circuit-state lookup) and the budget reservation script.

**Where.** `RedisRateLimiter`, `RedisSpendLedger` (`src/Ume.LlmGateway.Infrastructure/Stores/RateLimiter.cs`, `SpendLedger.cs`),
`GatewayRequestHandler.HandleAsync` steps 4–8 (the stages are in `RequestStages`), `BudgetService.ReserveAsync`.

**Approach.** The budget estimate depends on the routing result, which runs between the two. Options: reserve with an
upper-bound estimate computed from the requested model's most expensive target at rate-limit time (when no routing
rule is configured, which is the common case), falling back to today's two-step path when rules apply. Keep the
semantics: a rate-limited request must not reserve budget. One Lua script that checks the rate limit and reserves
only when the request is allowed keeps that rule atomically. Two ordering details:

- The PII check and the content guard run between the two steps today, so a request they refuse after the combined
  script must release its reservation (an extra round trip, but only on the refusal path).
- The request policy's prompt addition counts towards the estimate; it depends only on the key, so it can be computed
  before the combined script.

**Done when.** `audit` shows one Redis round trip before the first byte on the common path; budget and rate-limit
tests pass, including rules that change the model.

### 2. CPU per message at 6 vCPUs (cause found 2026-11-02; fixes open)

**Found.** Not the CPU layout: with 3 physical cores of its own the gateway costs the same 0.22 ms
(`benchmarks/compare/cores.sh`). The extra 0.04 ms per message is user-mode CPU from contention: spinning in contended
locks and in idle thread-pool workers, a lock on the provider client's shared default headers, and StackExchange.Redis
write collisions on its one connection; allocation and work per message do not change
(`benchmarks/compare/profile.sh`; details in
[performance.md](performance.md#cpu-per-message-at-6-vcpus-2026-11-02)). Next, one pull request each, measured with
both scripts at 4 and 6 vCPUs: set `User-Agent` per request instead of in `DefaultRequestHeaders` (done: the lock is
gone, CPU per message unchanged within noise); try a lower thread-pool spin limit; try two to four Redis connections.

**Evidence (before the analysis).** CPU per message is flat from 1 to 4 vCPUs (0.17–0.18 ms) and rises to 0.22 ms at 6, where efficiency
drops to 73 % (baseline 2026-11-02; on 2026-10-27, before #55 and #56, it was 0.20–0.21 and 0.25 ms, 72 %).
Part of it is the machine (two of the six threads share a physical core with the fake LLM and Postgres),
part may be contention (locks, thread pool, the single Redis multiplexer connection).

**Where.** Whole pipeline; suspects: `StackExchange.Redis` multiplexer (one connection for all commands),
`KeyAuthenticator`/`GatewayCatalog` caches, thread-pool settings, GC mode.

**Approach.** Profile under load in the compare stack: `dotnet-counters` (thread-pool queue length, lock contention,
GC pause time, allocation rate) and a `dotnet-trace` CPU sample at 6 vCPUs, 384 clients. Ideally re-run on a machine
with more cores so the 6-vCPU point does not share cores with the harness.

**Done when.** CPU per message at 6 vCPUs is within about 10 % of the 2-vCPU figure, or the remaining cause is
documented in `performance.md` (documented; the three fixes above are still to be measured).

### 3. Memory footprint under load (done 2026-11-02)

The 1.0–1.7 GB was Redis command tracing holding every traced request for 10 s, not the garbage collector; with a 1 GiB
container limit the gateway crashed. Redis command tracing is off by default now: peak memory at 64 clients 156 MB
instead of 1.59 GB, and 25–35 % more throughput. GC settings (DATAS off, `GCConserveMemory`, fixed heap count,
Workstation GC, 1 GiB and 512 MB limits) then differ by noise, so the runtime default stays. Details:
[performance.md](performance.md#memory-and-the-garbage-collector-2026-11-02); tool: `benchmarks/compare/gc.sh`. The
baseline of 2026-11-02 is measured with the fix.

### 4. Verify horizontal scaling

**Evidence.** Not measured yet. Each instance has its own usage writer and caches and shares Redis and Postgres, so
several instances should add up until Redis or Postgres saturates. At the 4-vCPU peak Redis used about half of its one
command thread, so it is the first shared component to watch.

**Approach.** Extend `benchmarks/compare` with 2 and 3 gateway replicas behind a small load balancer (nginx or HAProxy
container), same total vCPUs as one larger instance, and record throughput, p99 and Redis/Postgres CPU.

**Done when.** `performance.md` has a table for 1/2/3 instances and names the shared component that limits it.

### 5. Guard against regressions

**Approach.** Add a short CI job (or a documented pre-release step) that runs `--filter '*Pipeline*'` and the `audit`
and fails when allocations per request grow by more than 20 % or the request path gains a Postgres statement or a
Redis round trip. BenchmarkDotNet timing in shared CI is noisy, so gate on allocations and call counts, not time.
Timing is noisy on the WSL dev machine too: the same streaming benchmark measured 479 and 766 µs in two runs of one day,
while allocations agreed to the kilobyte. Actions minutes are limited, so a documented pre-release step is preferred to
a job on every pull request.

**Done when.** The job exists and is described in `docs/runbook.md` or `HANDOVER.md`.

### Lower priority

- **Byte-level SSE passthrough** (never decoding upstream events to strings): saves further allocations on the main
  streaming path; needs a byte-based `SseEvent` shared with the translator.
- **Responses + MCP relay** still builds a JSON tree per delta, as the Anthropic translator did before it moved to
  `Utf8JsonReader`/`Utf8JsonWriter`.

### Measured and dropped

- **Copying the request body per fallback attempt** (`DeepClone` in `GatewayRequestHandler.AttemptAsync`). A clone of a
  parsed `JsonNode` tree shares its string values, so it costs about 1 KB for a 1.3 MB image request and about 20 KB
  (5 % of the request's allocations) for a 64 KB chat, with no measurable time difference. Undoing the rewrite instead
  (pull request #51) was closed as not worth the extra code.
- **Benchmark harness flake:** a `Pipeline` run can report `NA` for the Redis rows when the Redis test container refuses
  connections at start; re-run the benchmark rather than reading it as a regression.

## Starting the next session

Suggested opening prompt:

> Read `docs/performance-improvement-plan.md` and `docs/performance.md`. Start with item 1 (rate limit and budget in one
> Redis round trip). Measure with `PLATFORMS="fake ume" benchmarks/compare/scale.sh` and the `audit`, implement the change with
> tests, then re-run the whole baseline (see *Measuring* in `docs/performance.md`) and update `docs/performance.md`,
> the website (`site/content.mjs`), `CHANGELOG.md` and this plan with the results.

Keep the conventions in `HANDOVER.md` (analyzers as errors, `[LoggerMessage]` logging, tests with xUnit v3 + Shouldly)
and update the README and getting-started docs when behaviour changes.
