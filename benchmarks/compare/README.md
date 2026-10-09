# Platform comparison: Ume LLM gateway vs. eneo

Runs the Ume LLM gateway and [eneo](https://github.com/eneo-ai/eneo) (v2.2.1) against **the same fake LLM**
(`src/Ume.LlmGateway.FakeLlm`, which answers instantly), so the numbers are the platforms' own cost per chat message.
A third target sends the same requests straight to the fake LLM: that is the floor, and everything above it is
platform overhead. Results and interpretation: [docs/performance.md](../../docs/performance.md#comparison-with-eneo).

## What is compared

| | Ume LLM gateway | eneo v2.2.1 |
|---|---|---|
| Request | `POST /v1/chat/completions` (`ume/chat` alias), virtual key | `POST /api/v1/conversations/` with the personal assistant, JWT from a password login |
| Image | `dotnet publish /t:PublishContainer` (Release, chiseled) | `backend/Dockerfile` from the v2.2.1 tag (production image, gunicorn + uvicorn workers) |
| Concurrency model | One process (all cores) | `NUM_WORKERS=4` gunicorn workers (default 3), one per core |
| Data | Postgres 17, Redis 7.4 | pgvector/pg13, Redis (as eneo's own compose files) |
| Features on the path | Key auth, rate limits, key/team/department budgets, routing, usage records | Auth, assistant/space resolution and permissions, conversation and message persistence, prompt assembly, token counting, litellm |
| Logging | Information (production default) | `LOGLEVEL=INFO` |

Both run with the same CPU allowance, pinned so nothing competes: platform process cores 4–7, its Postgres + Redis
8–9, fake LLM 0–1, load generator ([k6](https://k6.io/)) 2–3. One platform runs at a time on fresh, in-memory databases.

The two products are not the same kind of thing: the gateway is a stateless proxy, eneo is a chat application that
stores every conversation. The comparison shows what each costs on top of the LLM, not which is "better".

## Running

Needs Docker, the .NET SDK, python3 and bash (Linux, macOS or WSL). Build the three images once:

```bash
# from the repository root
dotnet publish src/Ume.LlmGateway.Gateway -c Release /t:PublishContainer -p:ContainerImageTag=compare
dotnet publish src/Ume.LlmGateway.FakeLlm -c Release /t:PublishContainer -p:ContainerImageTag=compare

# eneo v2.2.1, in a separate worktree/clone of https://github.com/eneo-ai/eneo
git fetch origin tag v2.2.1 --no-tags && git worktree add --detach ../eneo-v2.2.1 v2.2.1
docker build --build-arg OTEL_SERVICE_VERSION=v2.2.1 -t eneo-backend:v2.2.1 ../eneo-v2.2.1/backend
```

Then:

```bash
benchmarks/compare/run.sh                               # 1, 16 and 64 clients; 30 s per scenario (~20 min)
VUS="1 32" DURATION=60s benchmarks/compare/run.sh       # other loads
PLATFORMS="ume" benchmarks/compare/run.sh               # re-run one platform; other results are kept
```

**Scaling** (`scale.sh`, about 25 minutes) measures two things, for streaming chat by default:

- *Load:* concurrent clients 1, 2, 4 … 256 on fixed resources (4 application cores each). Shows where each platform
  saturates and what happens to latency and errors beyond that point.
- *Resources:* 1, 2, 4 and 8 application cores (eneo: one gunicorn worker per core; .NET follows the container's CPU
  set) under 128 clients. Shows whether more hardware buys proportionally more throughput. Postgres + Redis get four
  cores throughout so the database never limits the application.

```bash
benchmarks/compare/scale.sh
CORES="1 2 4" LOAD_VUS="1 16 128" STREAM=false benchmarks/compare/scale.sh
```

It writes `results/scale-report.md` and `results/scale/scale-data.json` (for charts).

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
| `lib.sh` | Shared shell helpers (start/seed platforms, k6, CPU and query sampling) |
| `k6/chat.js` | One chat message per iteration for `TARGET=ume|eneo|fake`, streaming or not |
| `eneo/seed.py` | Adds a completion model pointing at the fake LLM (adapted from eneo's own `e2e/seed.py`) |
| `report.py`, `scale_report.py` | Build `results/report.md` and `results/scale-report.md` |

To run **eneo behind the gateway** (eneo's model provider pointing at the gateway instead of the fake LLM), start the
`ume` profile first, then the `eneo` profile with `ENEO_LLM_ENDPOINT=http://ume-gateway:8080/v1`,
`ENEO_LLM_MODEL=ume/chat` and `ENEO_LLM_API_KEY=<virtual key>`. This is how the PII and fallback checks in
`docs/performance.md` were made.

The gateway database is seeded with `dotnet run --project benchmarks/Ume.LlmGateway.Benchmarks -- compare-seed`.
