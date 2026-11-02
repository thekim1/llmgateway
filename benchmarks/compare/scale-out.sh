#!/usr/bin/env bash
# Horizontal scaling: several gateway instances sharing one Postgres and one Redis. Do instances add up, and which shared
# component limits them first? See README.md in this folder.
#
#   ./scale-out.sh                          every configuration below, twice
#   CONFIGS="1x2 2x2 3x2" REPEATS=1 ./scale-out.sh
#
# "RxV" is R instances of V vCPUs each (hardware threads, 2k and 2k+1 share a physical core). They run on cores 8-13 of
# lib.sh's layout; Postgres and Redis get one thread each, the fake LLM 0-3, k6 4-7. There is no load balancer: k6 sends
# client n to instance n mod R, so the instances are loaded evenly and the measurement is the gateway tier plus the
# shared Redis and Postgres (a real balancer adds its own cost). Each configuration starts a fresh stack, warms up and
# runs 32 and 64 clients per vCPU, streaming. Results: results/scale-out/…, summary in results/scale-out/report.md.
set -euo pipefail
cd "$(dirname "$0")"

# name → CPU sets of instance 1, 2, 3 (separated by space)
declare -A LAYOUT=(
  [1x2]="8-9"
  [2x2]="8-9 10-11"
  [3x2]="8-9 10-11 12-13"
  [1x4]="8-11"
  [2x3]="8-10 11-13"
  [1x6]="8-13"
)
CONFIGS="${CONFIGS:-1x2 2x2 3x2 1x4 2x3 1x6}"
REPEATS="${REPEATS:-2}"
DURATION="${DURATION:-20s}"
WARMUP="${WARMUP:-10s}"

threads() { echo "$1" | tr ',' '\n' | awk -F- '{ s += (NF == 2 ? $2 - $1 + 1 : 1) } END { print s }'; }

for rep in $(seq "$REPEATS"); do
  for name in $CONFIGS; do
    read -r -a sets <<< "${LAYOUT[$name]}"
    export REPLICAS=${#sets[@]} APP_CPUS=${sets[0]} APP2_CPUS=${sets[1]:-10-11} APP3_CPUS=${sets[2]:-12-13}
    export FAKE_CPUS=0-3 PG_CPUS=14 REDIS_CPUS=15
    source ./lib.sh
    total=0
    for s in "${sets[@]}"; do total=$((total + $(threads "$s"))); done
    log "$name, run $rep: instances on ${sets[*]}, $total vCPUs"
    start_platform ume
    UME_TARGETS="http://ume-gateway:8080"
    if ((REPLICAS > 1)); then
      for i in $(seq 2 "$REPLICAS"); do
        "${COMPOSE[@]}" --profile ume up -d "ume-gateway-$i" >/dev/null
        wait_http "http://127.0.0.1:$((18079 + i))/v1/models" '^401$'
        UME_TARGETS+=",http://ume-gateway-$i:8080"
      done
    fi
    export UME_TARGETS
    warm_up ume
    for f in 32 64; do measure ume true $((f * total)) "$DURATION" "scale-out/$name-r$rep-vus_$((f * total))"; done
    stop_all
  done
done

python3 - <<'PY'
import glob, json, re
rows = []
for f in sorted(glob.glob('results/scale-out/*-vus_*.json')):
    if f.endswith('.cpu.json'):
        continue
    d = json.load(open(f)); c = json.load(open(f[:-5] + '.cpu.json'))
    name, rep, vus = re.match(r'.*/(.+)-r(\d+)-vus_(\d+)\.json', f).groups()
    seconds = d['requests'] / d['rps']
    busy = lambda keys: sum(c[k]['cpu_ns'] for k in keys if k in c) / 1e9 / seconds
    gateways = [k for k in c if k.startswith('ume-gateway')]
    r, v = (int(x) for x in name.split('x'))
    rows.append((r * v, name, int(vus), int(rep), d['rps'], d['duration_ms']['med'], d['duration_ms']['p(99)'],
                 busy(gateways) / d['rps'] * 1000, busy(gateways), busy(['ume-redis']), busy(['ume-db']),
                 busy(['fake-llm']), d.get('errorRate', 0)))
with open('results/scale-out/report.md', 'w') as out:
    out.write('# Several gateway instances on shared Postgres and Redis (streaming chat)\n\n')
    out.write('Instances "RxV" = R instances of V vCPUs. Busy = hardware threads the container kept busy on average; Redis '
              'runs commands on one thread, so 1.0 is its limit; Postgres had one thread, the fake LLM four.\n\n')
    out.write('| vCPUs in all | Instances | Clients | Run | Requests/s | Median ms | p99 ms | Gateway CPU ms/msg | Gateways busy | Redis busy | Postgres busy | Fake LLM busy | Errors |\n')
    out.write('|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n')
    for r in sorted(rows):
        out.write(f'| {r[0]} | {r[1]} | {r[2]} | {r[3]} | {r[4]:.0f} | {r[5]:.2f} | {r[6]:.1f} | {r[7]:.3f} | {r[8]:.2f} | {r[9]:.2f} | {r[10]:.2f} | {r[11]:.2f} | {r[12] * 100:.1f} % |\n')
print(open('results/scale-out/report.md').read())
PY
