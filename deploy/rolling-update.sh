#!/usr/bin/env bash
# Updates a stack started with compose.rolling.yaml one slot at a time, so the gateway and admin UI stay up
# (docs/zero-downtime-updates.md). Run it after changing the image references (deploy/.env) or unpacking a new
# release bundle over deploy/.
#
#   ./deploy/rolling-update.sh            # migrate, then replace gateway, gateway-b, adminapi, adminapi-b
#   ./deploy/rolling-update.sh --pull     # pull the images first
#   ./deploy/rolling-update.sh --force    # recreate every slot even if its image did not change (new certificates)
#
# Only for releases the upgrade guide marks "Rolling update: yes": the old version keeps running against the new
# schema until its slot is replaced. The optional Data API has one slot and is down while it restarts.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pull=0; force=()
settle="${UME_ROLLING_SETTLE_SECONDS:-15}"
while [ $# -gt 0 ]; do
  case "$1" in
    --pull) pull=1; shift;;
    --force) force=(--force-recreate); shift;;
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done

compose=(docker compose -f "$here/compose.prod.yaml" -f "$here/compose.rolling.yaml" --env-file "$here/.env")
services="$("${compose[@]}" config --services)"
pairs=(gateway:gateway-b adminapi:adminapi-b)

health() { # health SERVICE — prints the container's health status, or "missing"
  local id; id="$("${compose[@]}" ps -q "$1" 2>/dev/null || true)"
  [ -n "$id" ] || { echo missing; return; }
  docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id"
}

for pair in "${pairs[@]}"; do
  for svc in ${pair/:/ }; do
    status="$(health "$svc")"
    [ "$status" = healthy ] || {
      echo "error: $svc is $status. Start the stack with compose.rolling.yaml first (see docs/zero-downtime-updates.md):" >&2
      echo "  ${compose[*]} up -d --wait" >&2
      exit 1
    }
  done
done

if [ "$pull" = 1 ]; then
  "${compose[@]}" pull migrations gateway gateway-b adminapi adminapi-b
  if grep -qx dataapi <<<"$services"; then "${compose[@]}" pull dataapi; fi
fi

echo "==> Migrating the database"
"${compose[@]}" run --rm --no-deps migrations

replace() { # replace SERVICE OTHER — recreate SERVICE while OTHER serves
  local svc="$1" other="$2"
  [ "$(health "$other")" = healthy ] || { echo "error: $other is not healthy; not stopping $svc" >&2; exit 1; }
  echo "==> Replacing $svc ($other is serving)"
  "${compose[@]}" up -d --no-deps --wait --wait-timeout 600 "${force[@]}" "$svc"
  # Give the proxy time to take the new slot back into rotation (nginx retries a failed server after fail_timeout).
  sleep "$settle"
}
for pair in "${pairs[@]}"; do
  a="${pair%%:*}"; b="${pair##*:}"
  replace "$a" "$b"
  replace "$b" "$a"
done

if grep -qx dataapi <<<"$services"; then
  echo "==> Replacing dataapi (single slot: unavailable while it restarts)"
  "${compose[@]}" up -d --no-deps --wait --wait-timeout 600 "${force[@]}" dataapi
fi

echo "==> Done"
"${compose[@]}" ps
