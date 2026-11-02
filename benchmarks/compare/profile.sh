#!/usr/bin/env bash
# Inside the gateway at its peak on 4 and 6 vCPUs (own physical cores): user/kernel CPU, runtime counters (GC, locks,
# thread pool) and a stack sample. See README.md in this folder.
#
#   ./profile.sh
#   LAYOUTS="6own" ./profile.sh
#
# dotnet-counters and dotnet-trace run in an SDK container that shares the gateway's process namespace, on CPU 7
# (k6's set, idle enough at these loads). Results: results/profile/…, summary in results/profile/report.md.
set -euo pipefail
cd "$(dirname "$0")"

# name → gateway, fake LLM, Postgres and Redis CPU sets, clients (the scale.sh peak for that many vCPUs)
declare -A LAYOUT=([4own]="8-11 0-3 12-13 14-15 256" [6own]="8-13 0-3 14 15 384")
LAYOUTS="${LAYOUTS:-4own 6own}"
WARMUP="${WARMUP:-10s}"
SDK=mcr.microsoft.com/dotnet/sdk:10.0
OUT=$PWD/results/profile
TOOLS=$PWD/results/profile/tools

source ./lib.sh
results_dir results/profile
if [[ ! -x $TOOLS/dotnet-trace ]]; then
  log "installing dotnet-counters and dotnet-trace into $TOOLS"
  docker run --rm -v "$TOOLS":/tools "$SDK" sh -c \
    'dotnet tool install dotnet-counters --tool-path /tools && dotnet tool install dotnet-trace --tool-path /tools' >/dev/null
fi

gateway_cpu() { # → total user kernel (ns) of the gateway container, from the Docker API
  curl -s --unix-socket /var/run/docker.sock \
    "http://localhost/containers/$("${COMPOSE[@]}" ps -q ume-gateway)/stats?stream=false&one-shot=true" |
    python3 -c 'import json,sys; c=json.load(sys.stdin)["cpu_stats"]["cpu_usage"]; print(c["total_usage"], c["usage_in_usermode"], c["usage_in_kernelmode"])'
}
sidecar() {
  docker run --rm --pid "container:$("${COMPOSE[@]}" ps -q ume-gateway)" --cap-add SYS_PTRACE --cpuset-cpus 7 \
    -e TMPDIR=/proc/1/root/tmp -e DOTNET_ROOT=/usr/share/dotnet -v "$TOOLS":/tools -v "$OUT":/out "$SDK" "$@"
}

for name in $LAYOUTS; do
  read -r APP_CPUS FAKE_CPUS PG_CPUS REDIS_CPUS VUS <<< "${LAYOUT[$name]}"
  export APP_CPUS FAKE_CPUS PG_CPUS REDIS_CPUS
  source ./lib.sh
  log "$name: gateway $APP_CPUS, $VUS clients"
  start_platform ume
  warm_up ume
  k6 ume true "$VUS" 75s "profile/$name-k6" > "$OUT/$name-k6.txt" 2>&1 &
  load=$!
  sleep 15
  before=$(gateway_cpu); t0=$(date +%s%N)
  sidecar /tools/dotnet-counters collect -p 1 --duration 00:00:20 --refresh-interval 2 --format csv -o "/out/$name-counters.csv" \
    --counters System.Runtime,Microsoft.AspNetCore.Hosting,System.Net.Sockets >/dev/null 2>&1
  after=$(gateway_cpu); t1=$(date +%s%N)
  echo "$name $before $after $(( (t1 - t0) / 1000000 ))" > "$OUT/$name-cpu.txt"
  # Sampled thread time: which code each thread is in, about 100 times a second (wall clock, not CPU).
  sidecar /tools/dotnet-trace collect -p 1 --profile dotnet-sampled-thread-time,dotnet-common --duration 00:00:00:12 \
    --format Speedscope -o "/out/$name.nettrace" > "$OUT/$name-trace.log" 2>&1
  wait "$load"
  stop_all
done

python3 profile_report.py results/profile $LAYOUTS
