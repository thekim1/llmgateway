# Performance improvement plan (handoff)

Work list for the next round of data-plane performance work, written so it can be picked up in a new session without
the history. Each item has the evidence that motivates it, where to look, a suggested approach, and how to prove it
worked. All figures are from the baseline of 2026-10-27 in [performance.md](performance.md#results-baseline-2026-10-27)
(8 cores / 16 threads, Docker on WSL2; "vCPU" means one hardware thread). Re-measure the whole baseline in one session
after a change, and replace the figures there and here, so numbers never come from mixed runs.

## Where we stand

| | Value | Source |
|---|---|---|
| Gateway overhead per message, 1 client (median) | 0.97 ms non-streaming, 1.27 ms streaming | `run.sh` |
| Peak throughput, streaming, 1 / 2 / 4 / 6 vCPUs | 4 751 / 9 616 / 17 772 / 20 550 msg/s | `scale.sh`, resource sweep |
| CPU per message at those peaks | 0.21 / 0.20 / 0.21 / 0.25 ms | same |
| What limits the peak | The gateway's own CPU (3.6 of 4 vCPUs busy); fake LLM, Postgres and Redis well below their limits | harness check in the reports |
| Redis round trips per request | 2 before the first byte, 1 after (9 commands, 21 counting those inside Lua scripts) | `audit`, `run.sh` |
| Postgres on the request path | 0 statements (warm caches); 0.2–0.7 per message in the background writer under load | `audit`, `run.sh` |
| Memory of the gateway process under load | 1.0–1.7 GB at 16–64 clients | `run.sh` |
| Errors under overload (256 clients, 4 vCPUs) | 0 % (median 12.5 ms) | `scale.sh` |

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

### 1. CPU per message at 6 vCPUs

**Evidence.** CPU per message is flat from 1 to 4 vCPUs (0.20–0.21 ms) and rises to 0.25 ms at 6, where efficiency
drops to 72 %. Part of it is the machine (two of the six threads share a physical core with the fake LLM and Postgres),
part may be contention (locks, thread pool, the single Redis multiplexer connection).

**Where.** Whole pipeline; suspects: `StackExchange.Redis` multiplexer (one connection for all commands),
`KeyAuthenticator`/`GatewayCatalog` caches, thread-pool settings, GC mode.

**Approach.** Profile under load in the compare stack: `dotnet-counters` (thread-pool queue length, lock contention,
GC pause time, allocation rate) and a `dotnet-trace` CPU sample at 6 vCPUs, 384 clients. Ideally re-run on a machine
with more cores so the 6-vCPU point does not share cores with the harness.

**Done when.** CPU per message at 6 vCPUs is within about 10 % of the 2-vCPU figure, or the remaining cause is
documented in `performance.md`.

### 2. Rate limit and budget in one Redis round trip

**Evidence.** Two Redis round trips remain before the first byte: the rate-limit script (pipelined with the
circuit-state lookup) and the budget reservation script.

**Where.** `RedisRateLimiter`, `RedisSpendLedger` (`src/Ume.LlmGateway.Infrastructure/Stores/RateLimiter.cs`, `SpendLedger.cs`),
`GatewayRequestHandler` steps 4–8, `BudgetService.ReserveAsync`.

**Approach.** The budget estimate depends on the routing result, which runs between the two. Options: reserve with an
upper-bound estimate computed from the requested model's most expensive target at rate-limit time (when no routing
rule is configured, which is the common case), falling back to today's two-step path when rules apply. Keep the
semantics: a rate-limited request must not reserve budget.

**Done when.** `audit` shows one Redis round trip before the first byte on the common path; budget and rate-limit
tests pass, including rules that change the model.

### 3. Memory footprint under load

**Evidence.** The gateway process grows to 1.0–1.7 GB at 16–64 concurrent clients (server GC keeps large heaps on many
cores). Not a leak, but it sets the container memory limit and the cost per instance.

**Approach.** Measure with `DOTNET_GCHeapAffinitizeMask`/`DOTNET_GCHeapCount`, DATAS (`DOTNET_GCDynamicAdaptationMode=1`)
and `DOTNET_GCConserveMemory`, against throughput and p99. Pick a default for `deploy/compose.prod.yaml` and document it.

**Done when.** Steady-state memory at 64 clients is at most half of today's with less than 5 % throughput loss, or the
trade-off is documented.

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

**Done when.** The job exists and is described in `docs/runbook.md` or `HANDOVER.md`.

### Lower priority

- **Byte-level SSE passthrough** (never decoding upstream events to strings): saves further allocations on the main
  streaming path; needs a byte-based `SseEvent` shared with the translator.
- **Responses + MCP relay** still builds a JSON tree per delta, as the Anthropic translator did before it moved to
  `Utf8JsonReader`/`Utf8JsonWriter`.

## Starting the next session

Suggested opening prompt:

> Read `docs/performance-improvement-plan.md` and `docs/performance.md`. Start with item 1 (CPU per message at 6
> vCPUs). Measure with `PLATFORMS="fake ume" benchmarks/compare/scale.sh` and the `audit`, implement the change with
> tests, then re-run the whole baseline (see *Measuring* in `docs/performance.md`) and update `docs/performance.md`,
> the website (`site/content.mjs`), `CHANGELOG.md` and this plan with the results.

Keep the conventions in `HANDOVER.md` (analyzers as errors, `[LoggerMessage]` logging, tests with xUnit v3 + Shouldly)
and update the README and getting-started docs when behaviour changes.
