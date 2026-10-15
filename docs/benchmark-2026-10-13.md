# Benchmark: Ume gateway vs. eneo, 2026-10-13

A full comparison run after the streaming changes in [streaming-findings.md](streaming-findings.md). It has three parts:

1. **Instant fake LLM** (`benchmarks/compare/run.sh`, k6). This measures what each platform costs per message.
2. **Fake LLM with real-provider timing** (`benchmarks/compare/latency.sh`, the new stream probe). This measures what a
   streaming user experiences: time to first token (TTFT), gaps between chunks, and the whole answer.
3. **A realistic long conversation:** a ten-page document in five messages (about 10 000 tokens), with a model that
   takes longer to read long prompts, for 1–256 users.

Setup is as in [performance.md](performance.md#comparison-with-eneo): eneo v2.2.1 (production image, 4 gunicorn
workers) and the gateway (Release container built from the working tree on 2026-10-13) each get the same 4 pinned
cores, with 2 cores for their Postgres + Redis. The load generator runs on its own cores. The host is an AMD Ryzen 7
9800X3D running Docker Desktop on WSL2. Raw results are in `benchmarks/compare/results/2026-10-13/`.

## Summary

- **With a realistic LLM, the gateway adds nothing a user can notice.** At 1–256 concurrent streams, its median TTFT
  is within ±5 ms of the LLM called directly, and the gaps between chunks are the same as the LLM's own (p99 36 ms,
  never ≥ 250 ms). Its CPU cost is 9–18 ms per 5.6-second answer under load.
- **eneo adds 90–130 ms to TTFT at low load. Under load, users see long silences.** At 16 streams, eneo's TTFT p95 is
  3.6 s (LLM: 0.5 s). At 64 streams its TTFT p95 is 17.7 s and 118 mid-answer gaps were ≥ 250 ms. At 256 streams,
  TTFT is 44 s, 81 streams failed with HTTP 500, and gaps reached 15 s.
- **A realistic chat with a long document gives the same picture** (section 3). The request is a 10-page document in a
  5-message conversation (47 KB, about 10 000 tokens), and the answer is 500 words (about 14 s). With no jitter, the
  gateway adds **2 ms** to TTFT; eneo adds 62 ms at 1 user and has a TTFT p95 of 9.7 s at 32 users. Through the gateway,
  256 concurrent users get the LLM's own timing. eneo slows down from 8 users, and at 256 users 122 conversations
  failed with HTTP 500.
- **Provider stalls pass through unchanged.** When the LLM pauses 1.5 s now and then, both platforms show the same
  pauses as the LLM itself, and neither adds any.
- **The gateway's peak throughput is ~25 % lower than on 2026-10-09** (8 700 vs 11 700 non-streamed messages/s at 64
  clients). Its own CPU per message is unchanged, so this is not the streaming work. Most likely it is the usage
  writer: see *Regression to check* below.

## 1. Instant fake LLM (k6)

Overhead = platform median minus the fake LLM's median at the same load. CPU is per message.

| Non-streaming | Clients | Req/s | Median | Overhead | p99 | CPU app | CPU DB+Redis | Postgres stmts | Failed |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Ume gateway | 1 | 731 | 1.29 ms | 1.04 ms | 1.59 ms | 2.18 ms | 0.53 ms | 2.0 | 0 |
| eneo | 1 | 18 | 49.7 ms | 49.5 ms | 310 ms | 48.8 ms | 7.0 ms | 71.8 | 0 |
| Ume gateway | 16 | 7 648 | 1.63 ms | 1.39 ms | 14.9 ms | 0.39 ms | 0.16 ms | 1.0 | 0 |
| eneo | 16 | 35 | 182 ms | 182 ms | 2.39 s | 55.8 ms | 15.7 ms | 72.0 | 0 |
| Ume gateway | 64 | 8 860 | 3.27 ms | 2.17 ms | 40.8 ms | 0.24 ms | 0.12 ms | 1.0 | 0 |
| eneo | 64 | 25 | 446 ms | 445 ms | 42.1 s | 58.3 ms | 40.7 ms | 71.4 | **22** |

| Streaming | Clients | Req/s | Median | Overhead | TTFB | CPU app | CPU DB+Redis | Postgres stmts | Failed |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Ume gateway | 1 | 585 | 1.61 ms | 1.33 ms | 1.29 ms | 2.64 ms | 0.58 ms | 2.0 | 0 |
| eneo | 1 | 14 | 63.2 ms | 62.9 ms | 48.4 ms | 63.4 ms | 7.9 ms | 73.8 | 0 |
| Ume gateway | 16 | 6 337 | 2.25 ms | 1.87 ms | 1.71 ms | 0.50 ms | 0.19 ms | 1.1 | 0 |
| eneo | 16 | 56 | 169 ms | 168 ms | 120 ms | 70.8 ms | 5.5 ms | 73.9 | 0 |
| Ume gateway | 64 | 8 307 | 4.75 ms | 3.03 ms | 3.42 ms | 0.32 ms | 0.14 ms | 1.0 | 0 |
| eneo | 64 | 47 | 861 ms | 860 ms | 566 ms | 76.0 ms | 30.9 ms | 74.0 | 0 |

The gateway costs about 1–3 ms and 0.3 ms of CPU per message; eneo costs 50–860 ms and 50–76 ms of CPU. eneo's
numbers match 2026-10-09 within noise. This time eneo also failed 22 non-streamed requests at 64 clients (timeouts or
pool exhaustion, as described in performance.md).

## 2. Real-provider timing (stream probe)

The fake LLM answers like a mid-sized hosted model: 400 ms to the first token, then one word every 25 ms ±30 %
(about 40 words/s), 200 words per answer, so about 5.6 s per answer. The probe streams chat messages back to back
for 40 s per load and times every event from the client's side.

| Clients | Platform | Streams/s | Errors | TTFT p50 | TTFT p95 | TTFT added (p50) | Gap p99 | Gap max | Gaps ≥ 250 ms | Gaps ≥ 1 s | Whole answer p50 | p95 | CPU per answer |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | LLM directly | 0.2 | 0 | 382 ms | 521 ms | – | 35.8 ms | 38 ms | 0 | 0 | 5.65 s | 5.86 s | – |
| 1 | Ume gateway | 0.2 | 0 | 360 ms | 513 ms | ≈0 (jitter) | 36.4 ms | 38 ms | 0 | 0 | 5.62 s | 5.77 s | 142 ms¹ |
| 1 | eneo | 0.2 | 0 | 516 ms | 566 ms | +133 ms | 36.3 ms | 43 ms | 0 | 0 | 5.71 s | 5.86 s | 266 ms¹ |
| 16 | LLM directly | 2.8 | 0 | 405 ms | 507 ms | – | 36.0 ms | 39 ms | 0 | 0 | 5.62 s | 5.79 s | – |
| 16 | Ume gateway | 2.8 | 0 | 405 ms | 511 ms | +0.7 ms | 36.0 ms | 48 ms | 0 | 0 | 5.64 s | 5.77 s | 52 ms¹ |
| 16 | eneo | 2.5 | 0 | 492 ms | **3.65 s** | +87 ms | 36.7 ms | 349 ms | 11 | 0 | 5.70 s | 8.80 s | 184 ms |
| 64 | LLM directly | 11.2 | 0 | 399 ms | 512 ms | – | 35.9 ms | 40 ms | 0 | 0 | 5.60 s | 5.75 s | – |
| 64 | Ume gateway | 11.3 | 0 | 398 ms | 511 ms | −0.8 ms | 35.9 ms | 41 ms | 0 | 0 | 5.58 s | 5.73 s | 18 ms |
| 64 | eneo | 7.0 | 0 | 804 ms | **17.7 s** | +406 ms | 42.9 ms | 502 ms | 118 | 0 | 6.04 s | 23.0 s | 160 ms |
| 256 | LLM directly | 44.8 | 0 | 407 ms | 516 ms | – | 35.9 ms | 41 ms | 0 | 0 | 5.60 s | 5.76 s | – |
| 256 | Ume gateway | 45.0 | 0 | 411 ms | 520 ms | +4.0 ms | 36.0 ms | 45 ms | 0 | 0 | 5.60 s | 5.76 s | 9 ms |
| 256 | eneo | 4.9 | **81** | **44.2 s** | 55.5 s | +43.8 s | 61.8 ms | **15.0 s** | 283 | 131 | 50.1 s | 60.7 s | 161 ms |

¹ At low load the CPU per answer includes idle background work spread over few answers. The 64- and 256-client rows
show the real marginal cost.

What this shows:

- **TTFT:** the gateway's TTFT tracks the LLM's own at every load (the ±20 ms at 1 client is the fake LLM's jitter over
  a handful of samples). eneo starts 90–130 ms later at low load. Past about 16 concurrent streams, it queues new
  conversations behind running ones: the median holds, but the 95th percentile climbs from 3.6 s at 16 streams to
  17.7 s at 64.
- **Chunk gaps:** through the gateway, gaps are the LLM's own pace at all loads; the largest gap seen was 48 ms. eneo
  keeps the pace for most of the answer but has occasional silences of 250–500 ms at 16–64 streams. At 256 streams it
  breaks down: 15-second silences and HTTP 500 errors.
- **Throughput:** with 5.6 s answers, 256 streams through the gateway run at the LLM's full rate (45 answers/s) using
  about 9 ms of CPU per answer. eneo tops out at about 7 answers/s on the same cores.

### Provider stalls (profile `stalls`, 16 clients)

The same timing, plus a 1 % chance per word that the LLM pauses for 1.5 s (as Azure's content filter can do).

| Platform | TTFT p50 | Gaps ≥ 1 s | Gap max | Whole answer p50 | Added over the LLM |
|---|---:|---:|---:|---:|---:|
| LLM directly | 377 ms | 164 | 1.54 s | 8.57 s | – |
| Ume gateway | 403 ms | 167 | 1.54 s | 8.62 s | +45 ms |
| eneo | 537 ms | 149 | 1.55 s | 8.72 s | +150 ms |

Both platforms relay a provider's pause as it is: they don't add pauses and they don't lengthen them. The counts
differ only because each run makes a slightly different number of answers. So when users see long gaps through the
gateway in production, the cause is upstream. The gateway's new `ume.gateway.stream.max_upstream_gap` metric shows
which provider it is.

## 3. Realistic conversation with a long document

The earlier parts send one short question. Real use is often longer: a user attaches a document and asks follow-up
questions. This scenario sends that, to an LLM that takes longer to read a long prompt.

**Request** (`PROBE_PAYLOAD=document` in the probe):
- A system prompt and five messages: a user message with a ten-page planning document, an assistant answer, a
  follow-up question, a second answer, and a final question.
- About 5 000 words of document text (500 per page): 47 KB of JSON, about 10 000 input tokens.
- The text is deterministic and contains nothing the PII scanner reacts to, so every platform does the same work.
- eneo takes one question per call (it keeps history in its own database), so it gets the same text as one question.
  The same tokens reach the model. Its fake model's context limit was raised to 128 000 tokens (like gpt-4o).

**LLM timing:**
- 500 ms base time to first token, plus 60 ms per 1 000 input tokens of prefill (`PrefillMsPer1kInputTokens`): about
  1.1 s before the first token.
- Then a 500-word answer at 25 ms ±30 % per word, about 14 s per answer.
- Every user sends a new conversation as soon as the previous answer ends, for 60 s per load.

| Users | Platform | Answers/s | Errors | TTFT p50 | TTFT p95 | TTFT added (p50) | Gap max | Gaps ≥ 250 ms | Whole answer p50 | p95 | CPU per answer |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | LLM directly | 0.1 | 0 | 1.30 s | 1.37 s | – | 39 ms | 0 | 14.2 s | 14.7 s | – |
| 1 | Ume gateway | 0.1 | 0 | 1.23 s | 1.43 s | jitter² | 38 ms | 0 | 14.4 s | 14.7 s | 315 ms¹ |
| 1 | eneo | 0.1 | 0 | 1.19 s | 1.48 s | jitter² | 53 ms | 0 | 14.3 s | 14.7 s | 506 ms¹ |
| 8 | LLM directly | 0.6 | 0 | 1.07 s | 1.40 s | – | 39 ms | 0 | 14.2 s | 14.5 s | – |
| 8 | Ume gateway | 0.6 | 0 | 1.11 s | 1.47 s | jitter² | 50 ms | 0 | 14.3 s | 14.6 s | 160 ms¹ |
| 8 | eneo | 0.5 | 0 | 1.36 s | 1.98 s | +298 ms | 310 ms | 2 | 14.5 s | 15.0 s | 436 ms¹ |
| 32 | LLM directly | 2.2 | 0 | 1.13 s | 1.45 s | – | 42 ms | 0 | 14.2 s | 14.6 s | – |
| 32 | Ume gateway | 2.2 | 0 | 1.10 s | 1.41 s | jitter² | 39 ms | 0 | 14.2 s | 14.5 s | 70 ms |
| 32 | eneo | 1.9 | 0 | 1.43 s | **7.69 s** | +298 ms | 362 ms | 43 | 14.5 s | 20.6 s | 368 ms |
| 64 | LLM directly | 4.4 | 0 | 1.18 s | 1.46 s | – | 41 ms | 0 | 14.2 s | 14.6 s | – |
| 64 | Ume gateway | 4.4 | 0 | 1.17 s | 1.45 s | jitter² | 45 ms | 0 | 14.2 s | 14.5 s | 41 ms |
| 64 | eneo | 3.7 | 0 | 1.69 s | **6.89 s** | +510 ms | 494 ms | 218 | 14.8 s | 20.0 s | 312 ms |
| 128 | LLM directly | 8.9 | 0 | 1.13 s | 1.45 s | – | 41 ms | 0 | 14.2 s | 14.5 s | – |
| 128 | Ume gateway | 8.9 | 0 | 1.15 s | 1.46 s | +19 ms | 45 ms | 0 | 14.2 s | 14.6 s | 28 ms |
| 128 | eneo | 6.5 | 0 | 2.53 s | **12.3 s** | +1.4 s | 680 ms | 623 | 15.7 s | 26.1 s | 278 ms |
| 256 | LLM directly | 17.7 | 0 | 1.15 s | 1.46 s | – | 50 ms | 0 | 14.2 s | 14.5 s | – |
| 256 | Ume gateway | 17.7 | 0 | 1.14 s | 1.44 s | jitter² | 55 ms | 0 | 14.2 s | 14.5 s | 20 ms |
| 256 | eneo | 5.3 | **122** | **32.3 s** | 45.1 s | +31 s | **15.1 s** | 811 | 45.6 s | 75.9 s | 286 ms |

¹ At low load this includes idle background work spread over few answers.
² With ±30 % jitter on a 1.1 s wait, medians of different runs differ by up to ±100 ms. Medians below the LLM's own
are noise. The exact figures are in the jitter-free run below.

**Exact added time** (same request, no jitter, profile `document-exact`):

| Users | LLM directly, TTFT p50 | Ume gateway | eneo |
|---:|---:|---:|---:|
| 1 | 1 137 ms | 1 139 ms (**+2 ms**) | 1 198 ms (+62 ms) |
| 32 | 1 136 ms | 1 138 ms (**+2 ms**), p95 1 163 ms | 1 556 ms (+420 ms), p95 **9 674 ms** |

What this shows:

- **The gateway's cost does not grow with the request size in a way a user notices.** It reads, checks and forwards a
  47 KB conversation in about 2 ms. Its CPU per answer roughly doubles compared with a short question (20 ms vs 9 ms
  at 256 users), because it parses and forwards the larger body. That is still small next to a 14-second answer.
- **eneo's cost does grow with the document.** CPU per answer is 280–370 ms here, against about 160 ms for a short
  question. Most likely this is its repeated token counting (performance.md: 4–8 tiktoken passes per message), which
  now runs over 10 000 tokens. It also saturates earlier: TTFT p95 is 2 s at 8 users and 7.7 s at 32. At 256 users,
  48 % of conversations failed and answers stalled for 15 s.
- **Chunk gaps:** through the gateway, the largest gap was 55 ms at 256 users. That is the LLM's own pace.
- **Capacity:** the gateway carried 256 simultaneous document conversations (17.7 answers/s, about 177 000 input
  tokens/s) at the LLM's own timing, using about a fifth of a core per second of load. On the same four cores, eneo
  topped out at about 6.5 answers/s.

## Regression to check: gateway peak throughput

| Gateway, 64 clients | 2026-10-09 | 2026-10-13 run 1 | 2026-10-13 run 2 |
|---|---:|---:|---:|
| Non-streaming, req/s | 11 680 | 8 860 | 8 749 |
| Streaming, req/s | 10 566 | 8 307 | 8 475 |
| Gateway CPU per message (non-streaming) | 0.235 ms | 0.244 ms | 0.247 ms |
| Postgres + Redis CPU per message (non-streaming) | 0.097 ms | 0.122 ms | 0.125 ms |
| p99 at 16 clients (non-streaming) | 4.9 ms | 14.9 ms | 17.8 ms |

Two runs agree, so this is not noise.

- **What it isn't:** the gateway's own CPU per message is unchanged. Non-streaming requests drop as much as streaming
  ones, and they don't go through any of today's stream changes.
- **What it probably is:** performance.md found the 2026-10-09 peak was set by the batched usage writer, whose queue
  stayed full. Since then, usage records have gained columns: `UpstreamMs`, `TimeToFirstTokenMs`, and the MCP fields
  `McpServerId`, `McpMethod`, `McpTool` and `ParentRequestId`. Database CPU per message is up about 25 %, which fits
  heavier batch inserts lowering that cap.
- **To confirm:** watch the `ume.gateway.usage_queue.depth` gauge during the 64-client run, or run `audit` on this
  commit and on `7521b48`.
- **Fix if confirmed:** see performance.md: Postgres `COPY` for the batches, or more than one writer.
- **Fixed 2026-10-14:** the usage writer now uses `COPY`; 64 clients went to 15 083 req/s non-streaming and 10 878
  streaming ([performance.md](performance.md#usage-writer-copy-2026-10-14)).

Real deployments are far from this ceiling: with real LLM timing, 256 concurrent streams are 45 messages/s.

## Fake LLM: real-provider timing

The fake LLM has a new, optional `FakeLlm:Latency` section (all 0 = instant, as before):

| Setting | Used here |
|---|---|
| `FirstTokenMs` (time to first token; non-streamed answers wait for the whole answer) | 400 (part 3: 500) |
| `TokenIntervalMs` (per word) | 25 |
| `JitterPercent` | 30 |
| `PrefillMsPer1kInputTokens` (extra time to first token per 1 000 input tokens) | 60 (part 3 only) |
| `AnswerWords` | 200 (part 3: 500) |
| `StallProbability`, `StallMs` | 0.01, 1 500 (profile `stalls` only) |

With `stream_options.include_usage`, its chat chunks now carry `"usage":null` the way OpenAI's do, so instant runs also
exercise the real wire format. Locally, set the section in the AppHost user secrets (see [Local development](local-development.md#fake-llm-toggle)). In the compare
stack, use the `FAKE_*` variables of `latency.sh`.

## Re-running

```bash
dotnet publish src/Ume.LlmGateway.Gateway -c Release /t:PublishContainer -p:ContainerImageTag=compare
dotnet publish src/Ume.LlmGateway.FakeLlm -c Release /t:PublishContainer -p:ContainerImageTag=compare
dotnet build benchmarks/Ume.LlmGateway.Benchmarks -c Release
benchmarks/compare/run.sh                                                    # part 1, ~20 min
DURATION=40 benchmarks/compare/latency.sh                                    # part 2, ~12 min
PROFILE=stalls FAKE_STALL_P=0.01 FAKE_STALL_MS=1500 VUS=16 DURATION=40 benchmarks/compare/latency.sh
# part 3: ten-page document, increasing users, then without jitter for the exact added time (~40 min)
PROFILE=document PROBE_PAYLOAD=document FAKE_TTFT_MS=500 FAKE_PREFILL_MS_PER_1K=60 FAKE_ANSWER_WORDS=500 \
  VUS="1 8 32 64 128 256" DURATION=60 benchmarks/compare/latency.sh
PROFILE=document-exact PROBE_PAYLOAD=document FAKE_TTFT_MS=500 FAKE_PREFILL_MS_PER_1K=60 FAKE_ANSWER_WORDS=500 \
  FAKE_JITTER_PCT=0 VUS="1 32" DURATION=45 benchmarks/compare/latency.sh
```

Things to compare next time:

- gateway TTFT added ≤ 5 ms and 0 gaps ≥ 250 ms at every load in part 2;
- the stall profile's "added" stays at the level of noise;
- part 3: the gateway adds ≤ 5 ms to TTFT in `document-exact`, and has 0 gaps ≥ 250 ms at 256 users;
- the regression above is closed (≥ 11 000 req/s at 64 clients), or explained.
