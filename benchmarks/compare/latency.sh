#!/usr/bin/env bash
# What a streaming user experiences on each platform when the LLM behaves like a real one: the fake LLM waits before
# the first token and paces the answer word by word (FakeLlm:Latency). Measured with the stream probe (time to first
# token, gaps between events, whole stream), which k6 cannot do. See README.md in this folder.
#
#   ./latency.sh                                   400 ms to first token, 25 ms ±30 % per word, 200-word answers
#   PROFILE=stalls FAKE_STALL_P=0.01 FAKE_STALL_MS=1500 ./latency.sh
#   PLATFORMS="ume" VUS="1 64" DURATION=20 ./latency.sh
#   PROFILE=document PROBE_PAYLOAD=document FAKE_TTFT_MS=500 FAKE_PREFILL_MS_PER_1K=60 FAKE_ANSWER_WORDS=500 \
#     VUS="1 8 32 64 128 256" DURATION=60 ./latency.sh     ten-page document in a five-message conversation
#
# Needs the images from README.md and a Release build of benchmarks/Ume.LlmGateway.Benchmarks (the probe).
set -euo pipefail
cd "$(dirname "$0")"

PLATFORMS="${PLATFORMS:-fake ume eneo}"
VUS="${VUS:-1 16 64 256}"
DURATION="${DURATION:-30}" # seconds per scenario
PROFILE="${PROFILE:-realistic}"
export FAKE_TTFT_MS="${FAKE_TTFT_MS:-400}" FAKE_TOKEN_MS="${FAKE_TOKEN_MS:-25}" FAKE_JITTER_PCT="${FAKE_JITTER_PCT:-30}"
export FAKE_ANSWER_WORDS="${FAKE_ANSWER_WORDS:-200}" FAKE_STALL_P="${FAKE_STALL_P:-0}" FAKE_STALL_MS="${FAKE_STALL_MS:-0}"
export FAKE_PREFILL_MS_PER_1K="${FAKE_PREFILL_MS_PER_1K:-0}" PROBE_PAYLOAD="${PROBE_PAYLOAD:-question}"
source ./lib.sh

OUT="results/latency/$PROFILE"
BIN="$REPO/benchmarks/Ume.LlmGateway.Benchmarks/bin/Release/net10.0"
[[ -f "$BIN/Ume.LlmGateway.Benchmarks.dll" ]] || { echo "Build the benchmarks in Release first" >&2; exit 1; }
mkdir -p "$OUT"

url_of() { case $1 in fake) echo http://fake-llm:8080 ;; ume) echo http://ume-gateway:8080 ;; eneo) echo http://eneo-backend:8000/api/v1 ;; esac; }

# The probe runs as a container on the compose network, on the load generator's cores (like k6).
probe() { # platform vus seconds out-file
  docker run --rm --network platform-compare_default --cpuset-cpus "$K6_CPUS" -e PROBE_PAYLOAD -e ENEO_KEY="${ENEO_KEY:-}" -e ENEO_ASSISTANT="${ENEO_ASSISTANT:-}" \
    -v "$BIN:/probe:ro" -v "$PWD/$OUT:/out" --entrypoint /usr/share/dotnet/dotnet \
    mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra \
    /probe/Ume.LlmGateway.Benchmarks.dll stream-probe "$1" "$(url_of "$1")" "$2" "$3" "/out/$4" "${KEY:-}"
}

for platform in $PLATFORMS; do
  log "Starting $platform ($PROFILE: ${PROBE_PAYLOAD}, first token ${FAKE_TTFT_MS} ms + ${FAKE_PREFILL_MS_PER_1K} ms per 1k input tokens, ${FAKE_TOKEN_MS} ms/word ±${FAKE_JITTER_PCT} %, ${FAKE_ANSWER_WORDS} words, stalls p=${FAKE_STALL_P} × ${FAKE_STALL_MS} ms)"
  start_platform "$platform"
  probe "$platform" 8 10 "warmup-$platform.json" >/dev/null
  for vus in $VUS; do
    declare -A before=()
    for s in $(services_for "$platform"); do before[$s]=$(container_stats "$s" | cut -d' ' -f1); done
    probe "$platform" "$vus" "$DURATION" "$platform-vus_$vus.json"
    {
      echo "{"
      first=1
      for s in $(services_for "$platform"); do
        read -r cpu mem < <(container_stats "$s")
        [[ $first == 1 ]] || echo ","
        first=0
        printf '"%s": {"cpu_ns": %s, "memory_bytes": %s}' "$s" "$((cpu - before[$s]))" "$mem"
      done
      echo "}"
    } > "$OUT/$platform-vus_$vus.cpu.json"
    unset before
  done
done

stop_all
log "Report"
python3 latency_report.py "$OUT" | tee "$OUT/report.md"
