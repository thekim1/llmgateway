#!/usr/bin/env bash
# Garbage collector settings against memory, throughput and latency of the gateway. See README.md in this folder.
#
#   ./gc.sh                                   every configuration below, 16 and 64 clients, streaming and not
#   CONFIGS="default conserve5" ./gc.sh       some of them
#   VUS="64" DURATION=60s ./gc.sh
#
# Each configuration starts a fresh stack, warms up, then runs every scenario while the gateway container's memory is
# sampled every second. Results: results/gc/<config>/…, summary in results/gc/report.md.
set -euo pipefail
cd "$(dirname "$0")"

# name → GC_SERVER GC_DATAS GC_CONSERVE GC_HEAP_COUNT (compose.yaml passes them on as DOTNET_* variables, in hex) and
# optionally APP_MEM, the container's memory limit (the GC then keeps its heap under 75 % of it).
declare -A SETTINGS=(
  [default]="1 1 0 0"          # Server GC with DATAS, the runtime's default
  [no-datas]="1 0 0 0"         # Server GC, one heap per core (the default before .NET 9)
  [conserve5]="1 1 5 0"        # DATAS, and compact more to keep the heap smaller
  [conserve9]="1 1 9 0"        # the most compacting
  [heaps2]="1 0 0 2"           # Server GC with two fixed heaps
  [workstation]="0 0 0 0"      # Workstation GC (one heap, concurrent)
  [limit1g]="1 1 0 0 1g"       # the default under Kubernetes' 1Gi limit
  [limit1g-conserve5]="1 1 5 0 1g"
  [limit512m]="1 1 0 0 512m"   # the sizing stack's limit
)
CONFIGS="${CONFIGS:-default no-datas conserve5 conserve9 heaps2 workstation}"
VUS="${VUS:-16 64}"
DURATION="${DURATION:-30s}"
WARMUP="${WARMUP:-15s}"
source ./lib.sh

sample_memory() { # file: the gateway's memory in bytes, once a second, until killed
  while true; do container_stats ume-gateway | cut -d' ' -f2 >> "$1"; sleep 1; done
}

for config in $CONFIGS; do
  read -r GC_SERVER GC_DATAS GC_CONSERVE GC_HEAP_COUNT APP_MEM <<< "${SETTINGS[$config]:?unknown configuration $config}"
  APP_MEM=${APP_MEM:-0}
  export GC_SERVER GC_DATAS GC_CONSERVE GC_HEAP_COUNT APP_MEM
  log "$config (gcServer=$GC_SERVER, DATAS=$GC_DATAS, ConserveMemory=$GC_CONSERVE, HeapCount=$GC_HEAP_COUNT, memory limit $APP_MEM)"
  rm -rf "results/gc/$config"
  mkdir -p "results/gc/$config"
  chmod a+rwx "results/gc" "results/gc/$config" # k6 writes its summary here as another user
  start_platform ume
  container_stats ume-gateway | cut -d' ' -f2 > "results/gc/$config/idle.memory"
  warm_up ume
  for stream in false true; do
    for vus in $VUS; do
      out="gc/$config/stream_$stream-vus_$vus"
      : > "results/$out.memory"
      sample_memory "results/$out.memory" &
      sampler=$!
      measure ume "$stream" "$vus" "$DURATION" "$out"
      kill "$sampler" 2>/dev/null || true
      wait "$sampler" 2>/dev/null || true
    done
  done
  sleep 10 # after the load: what the gateway keeps
  container_stats ume-gateway | cut -d' ' -f2 > "results/gc/$config/after.memory" || echo 0 > "results/gc/$config/after.memory"
  docker inspect -f '{{.State.OOMKilled}} {{.RestartCount}} {{.State.Status}}' "$("${COMPOSE[@]}" ps -aq ume-gateway)" > "results/gc/$config/state"
done

stop_all
python3 - "$CONFIGS" <<'EOF' | tee results/gc/report.md
import json, sys
from pathlib import Path

root = Path("results/gc")
mb = lambda b: b / 1024 / 1024
print("| Configuration | Streaming | Clients | Requests/s | Median ms | p99 ms | Peak memory MB | Memory at end MB | CPU ms/req | Errors |")
print("|---|---|---|---|---|---|---|---|---|---|")
for config in sys.argv[1].split():
    folder = root / config
    for path in sorted(folder.glob("stream_*-vus_*.json")):
        if path.name.endswith(".cpu.json"):
            continue
        run = json.loads(path.read_text())
        cpu = json.loads(path.with_suffix(".cpu.json").read_text())
        samples = [int(x) for x in path.with_suffix(".memory").read_text().split() if x.strip()]
        cpu_ms = cpu["ume-gateway"]["cpu_ns"] / 1e6 / run["requests"] if run["requests"] else float("nan")
        stream, vus = path.stem.replace("stream_", "").split("-vus_")
        d = run["duration_ms"]
        print(f"| {config} | {stream} | {vus} | {run['rps']:.0f} | {d['med']:.2f} | {d['p(99)']:.2f} | {mb(max(samples, default=0)):.0f} "
              f"| {mb(cpu['ume-gateway']['memory_bytes']):.0f} | {cpu_ms:.3f} | {run['errorRate']:.2%} |")
    idle, after = (int((folder / f).read_text().split()[0]) for f in ("idle.memory", "after.memory"))
    oom, restarts, status = (folder / "state").read_text().split()
    note = "" if oom == "false" and status == "running" else f" (OOM killed: {oom}, {status})"
    print(f"| {config}{note} | idle before / 10 s after | | | | | {mb(idle):.0f} | {mb(after):.0f} | | |")
EOF
