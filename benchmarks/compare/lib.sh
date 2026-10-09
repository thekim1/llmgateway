#!/usr/bin/env bash
# Shared helpers for run.sh and scale.sh (sourced, not executed).
REPO="$(cd ../.. && pwd)"
PEPPER="compare-only-pepper-0123456789abcdef0123456789abcdef"
COMPOSE=(docker compose)
mkdir -p results

log() { printf '\n== %s\n' "$*"; }

wait_http() { # url, expected-pattern-of-status
  for _ in $(seq 1 120); do
    code=$(curl -s -o /dev/null -w '%{http_code}' "$1" || true)
    [[ "$code" =~ $2 ]] && return 0
    sleep 2
  done
  echo "Timed out waiting for $1" >&2
  return 1
}

# Cumulative CPU time (ns) and current memory (bytes) of one container, from the Docker API.
container_stats() {
  local id
  id=$("${COMPOSE[@]}" ps -q "$1")
  curl -s --unix-socket /var/run/docker.sock "http://localhost/containers/$id/stats?stream=false&one-shot=true" |
    python3 -c 'import json,sys; s=json.load(sys.stdin); print(s["cpu_stats"]["cpu_usage"]["total_usage"], s["memory_stats"].get("usage", 0))'
}

# Postgres statements and Redis commands executed so far (everything: request path and background work).
db_of() { case "$1" in ume) echo "ume-db gatewaydb" ;; eneo) echo "eneo-db eneo" ;; esac; }
redis_of() { case "$1" in ume) echo ume-redis ;; eneo) echo eneo-redis ;; esac; }
pg_calls() {
  read -r svc db < <(db_of "$1")
  "${COMPOSE[@]}" exec -T "$svc" psql -U postgres -d "$db" -tAc \
    "SELECT coalesce(sum(calls), 0) FROM pg_stat_statements WHERE query NOT ILIKE '%pg_stat_statements%'"
}
redis_calls() {
  "${COMPOSE[@]}" exec -T "$(redis_of "$1")" redis-cli INFO stats | tr -d '\r' | awk -F: '/^total_commands_processed/ {print $2}'
}

services_for() {
  case "$1" in
    ume) echo "ume-gateway ume-db ume-redis fake-llm" ;;
    eneo) echo "eneo-backend eneo-db eneo-redis fake-llm" ;;
    fake) echo "fake-llm" ;;
  esac
}

k6() { # target stream vus duration out
  "${COMPOSE[@]}" --profile tools run --rm -e TARGET="$1" -e STREAM="$2" -e VUS="$3" -e DURATION="$4" -e OUT="$5" -e KEY="${KEY:-}" \
    k6 run -q /scripts/chat.js 2>&1 | grep -vE '^\s*$|level=warn' || true
}

start_platform() {
  "${COMPOSE[@]}" --profile ume --profile eneo down -v --remove-orphans >/dev/null 2>&1 || true
  case "$1" in
    fake)
      "${COMPOSE[@]}" up -d fake-llm >/dev/null
      wait_http http://127.0.0.1:18090/ '^200$'
      ;;
    ume)
      "${COMPOSE[@]}" --profile ume up -d --wait ume-db ume-redis fake-llm >/dev/null
      "${COMPOSE[@]}" exec -T ume-db psql -U postgres -d gatewaydb -qc "CREATE EXTENSION IF NOT EXISTS pg_stat_statements" >/dev/null
      KEY=$(dotnet run -c Release --project "$REPO/benchmarks/Ume.LlmGateway.Benchmarks" -- compare-seed \
        "Host=127.0.0.1;Port=15432;Database=gatewaydb;Username=postgres;Password=postgres" "$PEPPER" http://fake-llm:8080/v1 2>/dev/null | tail -1)
      [[ "$KEY" == ume-sk-* ]] || { echo "Seeding the gateway database failed" >&2; exit 1; }
      "${COMPOSE[@]}" --profile ume up -d ume-gateway >/dev/null
      wait_http http://127.0.0.1:18080/v1/models '^401$' # up: answers, but wants a key
      ;;
    eneo)
      "${COMPOSE[@]}" --profile eneo up -d >/dev/null
      "${COMPOSE[@]}" --profile eneo up -d --wait eneo-db >/dev/null
      "${COMPOSE[@]}" exec -T eneo-db psql -U postgres -d eneo -qc "CREATE EXTENSION IF NOT EXISTS pg_stat_statements" >/dev/null
      # Migrations and seeding run before gunicorn binds the port, so any HTTP answer means ready.
      wait_http http://127.0.0.1:18000/api/v1/ '^[2-5][0-9][0-9]$'
      ;;
  esac
}

# Runs one k6 scenario and stores CPU (per container), memory, Postgres statements and Redis commands next to the
# k6 summary: results/<out>.json and results/<out>.cpu.json. <out> may contain a subfolder.
measure() { # platform stream vus duration out
  local platform=$1 out=$5 s cpu mem first pg_before=0 pg_after=0 redis_before=0 redis_after=0
  local -A before=()
  mkdir -p "results/$(dirname "$out")"
  for s in $(services_for "$platform"); do before[$s]=$(container_stats "$s" | cut -d' ' -f1); done
  # A failed probe (e.g. Postgres out of connections) must not abort the run: the count is then reported as null.
  if [[ $platform != fake ]]; then pg_before=$(pg_calls "$platform" || true); redis_before=$(redis_calls "$platform" || true); fi
  k6 "$platform" "$2" "$3" "$4" "$out"
  sleep 3 # let background writers (usage records) catch up so their statements are counted
  if [[ $platform != fake ]]; then pg_after=$(pg_calls "$platform" || true); redis_after=$(redis_calls "$platform" || true); fi
  {
    echo "{"
    first=1
    for s in $(services_for "$platform"); do
      read -r cpu mem < <(container_stats "$s")
      [[ $first == 1 ]] || echo ","
      first=0
      printf '"%s": {"cpu_ns": %s, "memory_bytes": %s}' "$s" "$((cpu - before[$s]))" "$mem"
    done
    printf ', "pg_statements": %s, "redis_commands": %s\n' "$(delta "$pg_before" "$pg_after" 0)" "$(delta "$redis_before" "$redis_after" 1)"
    echo "}"
  } > "results/$out.cpu.json"
}

delta() { # before after correction → after-before-correction, or null if a probe failed
  if [[ "$1" =~ ^[0-9]+$ && "$2" =~ ^[0-9]+$ ]]; then echo $(($2 - $1 - $3)); else echo null; fi
}

warm_up() { # platform
  k6 "$1" false 8 "$WARMUP" "warmup-$1" >/dev/null
  k6 "$1" true 8 "$WARMUP" "warmup-$1" >/dev/null
}

stop_all() { "${COMPOSE[@]}" --profile ume --profile eneo down -v --remove-orphans >/dev/null 2>&1 || true; }
