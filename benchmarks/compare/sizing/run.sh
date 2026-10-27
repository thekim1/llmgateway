#!/usr/bin/env bash
# Sizing test: is a small host enough? The gateway gets 0.5 vCPU and 512 MB, Postgres 1 vCPU and 1 GB (on a disk
# volume, not tmpfs), Redis 0.25 vCPU and 256 MB. The key has PII Allow and content guard Record; the fake LLM has a real
# provider's timing. Mixed traffic (k6/mix.js) at fixed rates, with CPU, memory, CPU throttling and the size of the usage
# table measured. Uses the images built for ../README.md. Results: ../results/sizing/.
#
#   RATES="10 50 100" DURATION=2m benchmarks/compare/sizing/run.sh   (defaults; about 10 minutes)
set -euo pipefail
cd "$(dirname "$0")"
REPO="$(cd ../../.. && pwd)"
PEPPER="compare-only-pepper-0123456789abcdef0123456789abcdef"
RATES="${RATES:-10 50 100}" DURATION="${DURATION:-2m}"
OUT=../results/sizing
COMPOSE=(docker compose)
mkdir -p "$OUT/k6" && chmod 777 "$OUT"

psqlc() { "${COMPOSE[@]}" exec -T db psql -U postgres -d gatewaydb -tAc "$1"; }
stats() { docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' compare-sizing-gateway-1 compare-sizing-db-1 compare-sizing-redis-1; }
cpu_stat() { # gateway cgroup: usage_usec nr_periods nr_throttled throttled_usec
  local id; id=$(docker inspect -f '{{.Id}}' compare-sizing-gateway-1)
  for f in /sys/fs/cgroup/system.slice/docker-$id.scope/cpu.stat /sys/fs/cgroup/docker/$id/cpu.stat; do
    [[ -f $f ]] && { awk '/^(usage_usec|nr_periods|nr_throttled|throttled_usec) /{printf "%s=%s ", $1, $2}' "$f"; return; }
  done
  echo "(cgroup cpu.stat not readable)"
}
sample() { while :; do stats | sed "s/^/$(date +%s) $1 /"; sleep 4; done; }

"${COMPOSE[@]}" --profile tools down -v --remove-orphans >/dev/null 2>&1 || true
"${COMPOSE[@]}" up -d --wait db redis fake-llm >/dev/null
KEY=""
for _ in 1 2 3 4 5; do # the host port forwards a moment after Postgres is healthy
  KEY=$(dotnet run -c Release --project "$REPO/benchmarks/Ume.LlmGateway.Benchmarks" -- compare-seed \
    "Host=127.0.0.1;Port=25432;Database=gatewaydb;Username=postgres;Password=postgres" "$PEPPER" http://fake-llm:8080/v1 \
    2>"$OUT/seed-error.log" | tail -1) || true
  [[ "$KEY" == ume-sk-* ]] && break
  sleep 3
done
[[ "$KEY" == ume-sk-* ]] || { echo "Seeding failed:" >&2; tail -20 "$OUT/seed-error.log" >&2; exit 1; }
psqlc "UPDATE \"VirtualKeys\" SET \"PiiPolicy\" = 'Allow', \"ContentGuardPolicy\" = 'Record'" >/dev/null
"${COMPOSE[@]}" up -d gateway >/dev/null
for _ in $(seq 60); do [[ $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28080/v1/models) == 401 ]] && break; sleep 1; done

{
  echo "# Sizing run $(date -u +%FT%TZ)"
  sample idle > "$OUT/stats.txt" & pid=$!; sleep 60; kill $pid
  echo "idle: $(stats | tr '\n' ';')"
  k6run() { # rate duration url model
    "${COMPOSE[@]}" --profile tools run --rm -e KEY="$KEY" -e RATE="$1" -e DUR="$2" -e URL="$3" -e MODEL="$4" \
      k6 run -q /scripts/mix.js 2>&1 | grep -E 'checks_failed|http_reqs\.\.|http_req_duration\.' || true
  }
  echo "## Fake LLM directly at 50/s (floor)"
  k6run 50 1m http://fake-llm:8080/v1/chat/completions fake-chat
  for rate in $RATES; do
    echo "## Gateway at $rate/s"
    before=$(cpu_stat)
    sample "$rate" >> "$OUT/stats.txt" & pid=$!
    k6run "$rate" "$DURATION" http://gateway:8080/v1/chat/completions ume/chat
    kill $pid
    echo "cpu.stat before: $before"
    echo "cpu.stat after:  $(cpu_stat)"
  done
  sleep 10 # the usage writer flushes in the background
  psqlc 'VACUUM ANALYZE "UsageRecords"' >/dev/null
  echo "## Usage table"
  psqlc "SELECT count(*) AS rows, pg_total_relation_size('\"UsageRecords\"') AS bytes_with_indexes,
           pg_total_relation_size('\"UsageRecords\"') / greatest(count(*), 1) AS bytes_per_row,
           count(*) FILTER (WHERE \"PiiCategories\" IS NOT NULL) AS with_pii, count(*) FILTER (WHERE \"GuardFlags\" IS NOT NULL) AS guard_flagged
         FROM \"UsageRecords\""
  echo "after: $(stats | tr '\n' ';')"
} | tee "$OUT/report.txt"

"${COMPOSE[@]}" --profile tools down -v --remove-orphans >/dev/null
