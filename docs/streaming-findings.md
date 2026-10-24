# Streaming findings (2026-10-13)

Why streams start fast and then fall behind with long chunk gaps: what was found, what was changed, and how to check
it again. Measured on the same host as [performance.md](performance.md) (AMD Ryzen 7 9800X3D, WSL2, .NET 10.0.12).

## Findings

| # | Finding | Status |
|---|---|---|
| 1 | **The fast path never ran for real OpenAI/Azure chunks.** The gateway always sets `stream_options.include_usage`, and then OpenAI and Azure send `"usage":null` in *every* chunk. The fast path only checked whether the text `"usage"` appeared, so every chunk was fully parsed into a DOM. The benchmark stub and FakeLlm never send `"usage":null`, which hid this. | **Fixed.** `ProviderJson.HasUsageValue` ignores `null` usage. Benchmark payload `Payloads.OpenAIWireStream` added. |
| 2 | **Compressed SSE.** The provider `HttpClient` decompresses automatically, so streamed calls sent `Accept-Encoding: gzip, deflate, br`. A provider, API manager or proxy that compresses `text/event-stream` holds tokens back until a compression block fills, which shows up as bursts separated by silences. | **Fixed.** Streamed calls use `ProviderHttpClient.StreamClient`, which sends no `Accept-Encoding`. A stream that arrives compressed anyway is still decoded. |
| 3 | **Azure content filter / reasoning.** In its default streaming mode, Azure OpenAI releases output in batches after filtering. Reasoning models are silent while they reason. Neither is gateway time. | Not code. Use the *asynchronous filter* on Azure deployments. Confirm with the metrics from #4. |
| 4 | **Gaps between chunks were not measured,** only time to first token. | **Added.** Per-stream metrics and a log line (see below). |
| 5 | Each chunk is decoded from UTF-8 to `string` and encoded back. Passthrough could forward the original bytes. | Open: byte-level SSE reader. |
| 6 | The Anthropic→OpenAI translator builds and serialises a JSON tree per token (≈600 µs and 1.27 MB per 300 chunks). The Responses+MCP relay does the same per delta. | Open: write chunks with `Utf8JsonWriter`. |
| 7 | One flush per event: after a stall, a burst of events goes out as many small TLS writes. Upstream uses HTTP/1.1 (one connection per concurrent stream). | Open, low priority. Benchmark before changing. |

No blocking calls, response compression or proxy buffering were found on the stream path. `deploy/proxy/nginx.conf.example`
has `proxy_buffering off`.

## Benchmarks

`dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- --filter '*ProviderBenchmarks*'`

| Provider leg, 300 chunks | Before | After |
|---|---:|---:|
| OpenAI wire format (`"usage":null` per chunk), the real-world case | 672 µs, 1 580 KB | **258 µs, 433 KB** (−62 %, −73 % allocated) |
| Old stub format (no `usage` per chunk) | 240 µs, 360 KB | 219 µs, 360 KB (unchanged) |
| Anthropic passthrough | 154 µs, 259 KB | 154 µs, 259 KB (unchanged) |
| Anthropic translated | 599 µs, 1 270 KB | 604 µs, 1 270 KB (unchanged, item 6) |

Gap timing on the full pipeline (`--filter '*PipelineBenchmarks.ChatStream*'`, 300 chunks): without it, in-memory 703 µs
and Redis 1 993 µs. With it, 709–731 µs and 1 868–1 890 µs. That is within noise (σ ≈ 150 µs) and allocations are
unchanged. The cost is two timestamp reads per event, plus one metric write per stream.

## New telemetry

Recorded once per stream that has at least two events, tagged `provider` and `endpoint`:

| Metric | Meaning |
|---|---|
| `ume.gateway.stream.max_upstream_gap` (ms) | Longest wait for the provider's next event after the first one. Large values point at the provider. |
| `ume.gateway.stream.max_write` (ms) | Longest time the gateway spent writing one event to the client. Large values point at the gateway (GC, thread pool) or a slow client. |
| `ume.gateway.stream.stalls` | Count of mid-stream waits of 1 s or more. |

Recorded once per streamed answer, tagged `outcome`, `provider` and `endpoint`: `ume.gateway.provider.time_to_first_token`
(ms), from sending the request to the provider that answered until its first event. Unlike `ume.gateway.time_to_first_token`
(until the first event reached the client) it leaves out failed fallback attempts, retries and the gateway's own time, so it
is the provider's own responsiveness. Provider stability in the admin UI uses the same figure.

A stream with a stall also logs, at Information level: `Request {RequestId}: stream from {Provider} stalled …`, with the
longest provider wait and longest write. Use it to find the slow request in *Usage > Requests and costs*.

Scope: `/v1/chat/completions`, `/v1/responses` and `/v1/messages` streams relayed by `StreamAsync`. Streamed
`/v1/responses` with hosted `mcp` tools is not covered yet.

## How to validate in the future

1. **Wire format:** the benchmark row "OpenAI wire format" stays close to the "Old stub format" row. If it jumps back
   to ~3× slower, a change has defeated the fast path again (test: `HotPathTests.Usage_value_detection_ignores_null_usage`).
2. **No compression on streams:** `HotPathTests.Streamed_calls_do_not_ask_for_compression` checks the request headers on
   the wire.
3. **Where gaps come from:** in production, compare p95 of `max_upstream_gap` and `max_write` per provider.
   - High `max_upstream_gap` with `max_write` near 0: the provider (or a filter or proxy in front of it) is the cause.
   - High `max_write`: the cause is in the gateway or on the client side. Check `dotnet-counters` for GC pause time and
     thread pool queue length.
4. **Azure:** after switching a deployment to the asynchronous filter, its `stalls` rate should drop.

## Full benchmark

With real-provider timing, the gateway adds ≤ 5 ms to time to first token and no chunk gaps at 1–256 streams, and
relays provider stalls unchanged: [benchmark-2026-10-13.md](benchmark-2026-10-13.md).

## Next steps

- Provider ping: a background probe in the gateway (`GET /models`, measuring DNS/connect/TLS/TTFB) with results in Redis
  and the admin UI, plus an optional opt-in 1-token completion for real TTFT.
- Rank the provider stability page on TTFT and `max_upstream_gap` instead of `LatencyMs`, which for streams is the whole
  generation time. That needs the gap stored per request (a `MaxChunkGapMs` column).
- Items 5–7 above, each with before/after benchmarks.
