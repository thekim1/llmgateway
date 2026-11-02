#!/usr/bin/env bash
# Gateway CPU per message by CPU layout: does it rise at 6 vCPUs because the gateway then shares physical cores with
# the harness, or in the gateway itself? See README.md in this folder.
#
#   ./cores.sh                              every layout below, twice
#   LAYOUTS="4own 6own" REPEATS=1 ./cores.sh
#
# Hardware threads 2k and 2k+1 are one physical core (lib.sh). Each layout runs on a fresh stack at 32 and 64 clients
# per gateway vCPU, streaming. Results: results/cores/…, summary in results/cores/report.md.
set -euo pipefail
cd "$(dirname "$0")"

# name → gateway, fake LLM, Postgres and Redis CPU sets (k6 stays on lib.sh's 4-7)
declare -A LAYOUT=(
  [4own]="8-11 0-3 12-13 14-15"      # the default: 2 physical cores of its own
  [6shared]="3,8-12 0-2 13 14-15"    # 6 threads, two of them shared with the fake LLM and Postgres
  [6own]="8-13 0-3 14 15"            # 3 physical cores of its own; Postgres and Redis one thread each
  [4own-1core]="8-11 0-3 14 15"      # as 4own with 6own's Postgres and Redis, to separate their effect
)
LAYOUTS="${LAYOUTS:-4own 6shared 6own 4own-1core}"
REPEATS="${REPEATS:-2}"
DURATION="${DURATION:-20s}"
WARMUP="${WARMUP:-10s}"

threads() { echo "$1" | tr ',' '\n' | awk -F- '{ s += (NF == 2 ? $2 - $1 + 1 : 1) } END { print s }'; }

for rep in $(seq "$REPEATS"); do
  for name in $LAYOUTS; do
    read -r APP_CPUS FAKE_CPUS PG_CPUS REDIS_CPUS <<< "${LAYOUT[$name]}"
    export APP_CPUS FAKE_CPUS PG_CPUS REDIS_CPUS
    source ./lib.sh
    log "$name, run $rep: gateway $APP_CPUS, fake LLM $FAKE_CPUS, Postgres $PG_CPUS, Redis $REDIS_CPUS"
    start_platform ume
    warm_up ume
    n=$(threads "$APP_CPUS")
    for f in 32 64; do measure ume true $((f * n)) "$DURATION" "cores/$name-r$rep-vus_$((f * n))"; done
    stop_all
  done
done

python3 - <<'PY'
import glob, json, re
rows = []
for f in sorted(glob.glob('results/cores/*-vus_*.json')):
    if f.endswith('.cpu.json'):
        continue
    d = json.load(open(f)); c = json.load(open(f[:-5] + '.cpu.json'))
    name, rep, vus = re.match(r'.*/(.+)-r(\d+)-vus_(\d+)\.json', f).groups()
    rows.append((name, int(vus), int(rep), d['rps'], d['duration_ms']['med'], d['duration_ms']['p(99)'],
                 c['ume-gateway']['cpu_ns'] / 1e6 / d['requests']))
with open('results/cores/report.md', 'w') as out:
    out.write('# Gateway CPU per message by CPU layout (streaming chat)\n\n')
    out.write('| Layout | Clients | Run | Requests/s | Median ms | p99 ms | Gateway CPU ms/req |\n|---|---:|---:|---:|---:|---:|---:|\n')
    for r in sorted(rows):
        out.write(f'| {r[0]} | {r[1]} | {r[2]} | {r[3]:.0f} | {r[4]:.2f} | {r[5]:.1f} | {r[6]:.3f} |\n')
print(open('results/cores/report.md').read())
PY
