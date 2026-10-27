# Platform comparison: Ume LLM gateway vs. eneo

Runs the Ume LLM gateway and [eneo](https://github.com/eneo-ai/eneo) (latest release, v2.2.3; `ENEO_VERSION` picks another) against **the same fake LLM**
(`src/Ume.LlmGateway.FakeLlm`, which answers instantly), so the numbers are the platforms' own cost per chat message.
A third target sends the same requests straight to the fake LLM: that is the floor, and everything above it is
platform overhead. Results and interpretation: [docs/performance.md](../../docs/performance.md#comparison-with-eneo).

## What is compared

| | Ume LLM gateway | eneo v2.2.3 |
|---|---|---|
| Request | `POST /v1/chat/completions` (`ume/chat` alias), virtual key | `POST /api/v1/conversations/`: with a service API key (`ENEO_AUTH=apikey`, the fair comparison) or as a signed-in user (default). See *The eneo message* below |
| Image | `dotnet publish /t:PublishContainer` (Release, chiseled) | `backend/Dockerfile` from the v2.2.3 tag (production image, gunicorn + uvicorn workers) |
| Concurrency model | One process (all cores) | `NUM_WORKERS=4` gunicorn workers (default 3), one per core, plus eneo's background worker container on the same cores |
| Data | Postgres 17, Redis 7.4 | pgvector/pg13, Redis (as eneo's own compose files) |
| Features on the path | Key auth, rate limits, key/team/department budgets, routing, usage records | Auth, assistant/space resolution and permissions, conversation and message persistence, prompt assembly, token counting, litellm |
| Logging | Information (production default) | `LOGLEVEL=INFO` |

Both run with the same CPU allowance, pinned so nothing competes. `run.sh`, `scale.sh` and `latency.sh` share one
layout (set in `lib.sh`; hardware threads on an 8-core / 16-thread machine, siblings 2k/2k+1):

| Component | Hardware threads | Why |
|---|---|---|
| Platform process | 8–11 (`APP_CPUS`) | 4 vCPUs; `scale.sh` varies it from 1 to 6 |
| Postgres | 12–13 (`PG_CPUS`) | Its own physical core |
| Redis | 14–15 (`REDIS_CPUS`) | Its own physical core; snapshots to disk off (`--save ""`), so no fork every minute under load |
| Fake LLM | 0–3 (`FAKE_CPUS`) | As many threads as the platform, telemetry off (`OTEL_SDK_DISABLED`), so it is never the limit |
| Load generator ([k6](https://k6.io/), stream probe) | 4–7 (`K6_CPUS`) | As many threads as the platform |

Straight to the fake LLM (the `fake` target) this harness serves about 55 000 streamed requests/s (2026-10-27), three
times what the gateway reaches on 4 vCPUs. Each report ends with a *harness check*: how many threads the fake LLM,
Postgres and Redis kept busy in every gateway scenario, so a run where one of them was the limit is visible. One
platform runs at a time on fresh, in-memory databases.

### The eneo message

Every eneo message is one `POST /api/v1/conversations/` with `{assistant_id, question, stream}`:

- **Question:** the same 110-character Swedish question every time (*"Vilka handlingar behöver jag skicka in för att
  ansöka om bygglov för ett uterum, och hur lång är handläggningstiden?"*), the gateway gets the same text.
- **A new conversation per message**, never a follow-up, so the prompt does not grow (like one gateway call).
- **Assistant:** no knowledge (collections, websites, integrations), no attachments, no tools; its model is the fake LLM
  (`eneo/seed.py`), called once per message through litellm. The answer is about 45 tokens.
- **Streaming or not** (`STREAM`): streamed answers are server-sent events (`first_chunk`, one `text` event per word,
  `token_usage`); k6 measures until the whole stream has arrived. Non-streamed answers are one JSON response; eneo then
  holds a database connection during the LLM call. `run.sh` measures both; `scale.sh` streaming only by default.
- eneo stores every question and answer, so its database grows during a run; the gateway stores usage metadata only.

How the message is authenticated is the important choice, because the two paths cost different amounts:

| | **Service API key** (`ENEO_AUTH=apikey`) | **Signed-in user** (default) |
|---|---|---|
| Who sends this in practice | External applications and integrations: the counterpart of a gateway virtual key | eneo's own web app (browser) |
| Header | `X-API-Key: sk_…` | `Authorization: Bearer <JWT>` from one password login per k6 run |
| Assistant | One in a shared space "Benchmark API", which the key is scoped to | The default user's personal assistant |
| Set-up | `eneo/seed_api_key.py`, through eneo's API as the Owner: space, assistant, key (`sk_`, ownership `service`, write permission, 90-day expiry because eneo requires an expiry or IP allowlist for such keys, rate limit raised to 100 000 000/hour so it is checked but never hit) | none |
| Extra work per message | Key lookup, guardrails (IP, origin, expiry), scope check, rate limit in Redis, `last_used_at` (at most every 15 min), an *API key used* audit event (default sample rate 100 %), queued in Redis and written to Postgres by the worker | none |

The gateway's virtual key does the same kinds of work (key lookup, rate limit, budget, usage record written in the
background), so the API-key path is the like-for-like comparison.

eneo's worker runs as in eneo's production compose (`RUN_AS_WORKER=true`, same image) and its CPU is counted with the
backend's. Without it the audit events would pile up in Redis unwritten.

The two products are not the same kind of thing: the gateway is a stateless proxy, eneo is a chat application that
stores every conversation. The comparison shows what each costs on top of the LLM, not which is "better".

## Running

Needs Docker, the .NET SDK, python3 and bash (Linux, macOS or WSL). Build the three images once:

```bash
# from the repository root
dotnet publish src/Ume.LlmGateway.Gateway -c Release /t:PublishContainer -p:ContainerImageTag=compare
dotnet publish src/Ume.LlmGateway.FakeLlm -c Release /t:PublishContainer -p:ContainerImageTag=compare

# eneo, from a clone of https://github.com/eneo-ai/eneo. Export with LF line endings: a Windows checkout
# (core.autocrlf) gives run.sh CRLF endings and the container exits with "./run.sh: not found".
git fetch origin tag v2.2.3 --no-tags
mkdir -p /tmp/eneo-v2.2.3 && git -c core.autocrlf=false archive v2.2.3 backend | tar -x -C /tmp/eneo-v2.2.3
docker build --build-arg OTEL_SERVICE_VERSION=v2.2.3 -t eneo-backend:v2.2.3 /tmp/eneo-v2.2.3/backend
```

`ENEO_VERSION=v2.2.1` (with an `eneo-backend:v2.2.1` image) runs an older release.

Then:

```bash
benchmarks/compare/run.sh                               # 1, 16 and 64 clients; 30 s per scenario (~12 min)
VUS="1 32" DURATION=60s benchmarks/compare/run.sh       # other loads
PLATFORMS="ume" benchmarks/compare/run.sh               # re-run one platform; other results are kept
ENEO_AUTH=apikey PLATFORMS=eneo benchmarks/compare/run.sh  # eneo through a service API key
```

**Scaling** (`scale.sh`, about 35 minutes for all three targets) measures two things, for streaming chat by default:

- *Load:* concurrent clients 1, 2, 4 … 256 on fixed resources (4 application cores each). Shows where each platform
  saturates and what happens to latency and errors beyond that point.
- *Resources:* 1, 2, 4 and 6 application vCPUs (eneo: one gunicorn worker per vCPU; .NET follows the container's CPU
  set), each on a fresh stack at several client counts; the report uses the best throughput. Shows whether more
  hardware buys proportionally more throughput. Postgres, Redis, the fake LLM and k6 keep their own cores. For 6 vCPUs
  the application borrows thread 3 from the fake LLM (left with 0–2) and thread 12 from Postgres (left with 13); on
  that smaller harness the 4-vCPU peak was the same within run-to-run noise (±3 %), so it is not a limit.

```bash
benchmarks/compare/scale.sh
CORES="1 2 4" LOAD_VUS="1 16 128" STREAM=false benchmarks/compare/scale.sh
```

It writes `results/scale-report.md` and `results/scale/scale-data.json` (for charts).

k6 keeps one connection per client open. eneo's gunicorn workers share one listening socket and each accepts a burst of
new connections at once, so with many workers (12 in one container) a few of them can end up with every client while
the rest stay idle. `NO_REUSE=true` makes k6 open a new connection per message, to check whether that affects a run.
Production traffic reaches eneo over kept-alive connections too (Traefik, frontend SSR).

eneo starts with one user. `eneo/seed_users.py` adds realistic data: USERS users (default 10 000) as an admin would
create them, then each opens their personal space and sends QUESTIONS messages (default 1) through eneo's API (it takes
a while: every user goes through eneo's own endpoints):

```bash
docker compose cp eneo/seed_users.py eneo-backend:/tmp/seed_users.py
docker compose exec -T -e USERS=10000 eneo-backend python /tmp/seed_users.py
```

**Streaming latency with real-provider timing** (`latency.sh`, about 10 minutes). The fake LLM is given a real
provider's timing: by default 400 ms to the first token, 25 ms ±30 % per word and 200-word answers (`FAKE_*` variables,
see the script). k6 cannot see inside a stream, so a small probe (`stream-probe` in the benchmark project, run as a
container on the k6 cores) measures what the user experiences: time to first token, gaps between events, and the whole
stream, at 1, 16, 64 and 256 clients.

```bash
dotnet build benchmarks/Ume.LlmGateway.Benchmarks -c Release   # the probe
benchmarks/compare/latency.sh
PROFILE=stalls FAKE_STALL_P=0.01 FAKE_STALL_MS=1500 VUS="16" benchmarks/compare/latency.sh
```

It writes `results/latency/<PROFILE>/report.md`.

**Sizing** (`sizing/run.sh`, about 10 minutes): is a small host enough? Its own stack (`sizing/compose.yaml`) limits the
gateway to 0.5 vCPU and 512 MB, Postgres to 1 vCPU and 1 GB on a disk volume and Redis to 0.25 vCPU and 256 MB, turns on
PII detection (*Allow*) and the content guard (*Record*), gives the fake LLM real-provider timing and sends mixed traffic
(`sizing/k6/mix.js`) at fixed rates. It reports CPU, memory, CPU throttling, latency against the fake LLM directly and
bytes per usage record in `results/sizing/report.txt`.

```bash
benchmarks/compare/sizing/run.sh
RATES="50" DURATION=5m benchmarks/compare/sizing/run.sh
```

The report is written to `results/report.md` (raw k6 summaries and CPU samples next to it). Per scenario it shows
requests/s, median/p95/p99 latency and overhead over the fake LLM, time to first byte, container CPU per request
(platform and its Postgres + Redis), Postgres statements and Redis commands per request (including background
writers and the commands inside Lua scripts), memory, and failed checks. Every response is checked for status 200 and
the fake LLM's answer, so an error can never pass as a fast response.

## Files

| File | Purpose |
|---|---|
| `compose.yaml` | Fake LLM, both platforms (profiles `ume`, `eneo`) and k6 (profile `tools`) |
| `run.sh` | Starts each platform on fresh databases, seeds it, warms up, runs the scenarios, samples CPU and query counts |
| `scale.sh` | Load and resource scaling (see above) |
| `sizing/` | A small host with PII and content guard on (see above) |
| `latency.sh`, `latency_report.py` | Streaming latency with real-provider timing (see above), via the stream probe |
| `lib.sh` | Shared shell helpers (start/seed platforms, k6, CPU and query sampling) |
| `k6/chat.js` | One chat message per iteration for `TARGET=ume|eneo|fake`, streaming or not |
| `eneo/seed.py` | Adds a completion model pointing at the fake LLM (adapted from eneo's own `e2e/seed.py`) |
| `eneo/seed_api_key.py` | Creates the shared space, assistant and service API key for `ENEO_AUTH=apikey` |
| `eneo/seed_users.py` | Adds benchmark users with personal spaces and chat history (see above) |
| `report.py`, `scale_report.py` | Build `results/report.md` and `results/scale-report.md` |

To run **eneo behind the gateway** (eneo's model provider pointing at the gateway instead of the fake LLM), start the
`ume` profile first, then the `eneo` profile with `ENEO_LLM_ENDPOINT=http://ume-gateway:8080/v1`,
`ENEO_LLM_MODEL=ume/chat` and `ENEO_LLM_API_KEY=<virtual key>`. This is how the PII and fallback checks in
`docs/performance.md` were made.

The gateway database is seeded with `dotnet run --project benchmarks/Ume.LlmGateway.Benchmarks -- compare-seed`.
