#!/usr/bin/env bash
# End-to-end test of updates without downtime on Docker Compose (docs/zero-downtime-updates.md): boots
# compose.prod.yaml + compose.rolling.yaml from a copy of deploy/, puts nginx in front with the configuration
# init-deployment.sh --rolling writes, and replaces every slot with rolling-update.sh --force while clients send GET and
# POST requests to the gateway and load the admin UI. Fails if a single request is lost.
#
#   deploy/tests/compose-rolling-e2e.sh      # uses the local images (ume-llm-gateway/*:<version>) or *_IMAGE
#
# Needs Docker (Linux host), python3, openssl and sudo for the file ownership init-deployment.sh sets.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project=ume-rolling-e2e; edge=ume-rolling-e2e-edge; port=18443
# The proxy reaches the slots at the host's own address, as a proxy on another machine would (not through loopback).
upstream="$(hostname -I | awk '{print $1}')"
cp -R "$repo/deploy" "$work/"
cd "$work"
compose=(docker compose -p "$project" -f deploy/compose.prod.yaml -f deploy/compose.rolling.yaml --env-file deploy/.env)
cleanup() {
  docker rm -f "$edge" >/dev/null 2>&1 || true
  "${compose[@]}" down -v >/dev/null 2>&1 || true
  cd / && sudo rm -r -f "$work"
}
trap cleanup EXIT

./deploy/init-deployment.sh --proxy --rolling --yes --oidc-authority https://login.microsoftonline.com/common/v2.0 \
  --gateway-host llm.test --admin-host llm-admin.test --upstream-host "$upstream" >/dev/null
mkdir -p edge
for h in llm.test llm-admin.test; do
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "edge/$h.key" -out "edge/$h.crt" -days 1 -subj "/CN=$h" \
    -addext "subjectAltName=DNS:$h" >/dev/null 2>&1
done
sed -e 's/# ssl_certificate/ssl_certificate/' -e "s/listen 443 ssl;/listen $port ssl;/" deploy/proxy/nginx.conf > edge/nginx.conf
grep -q "server $upstream:8444;" edge/nginx.conf || { echo "error: nginx.conf does not list the second gateway slot" >&2; exit 1; }

"${compose[@]}" up -d --wait --wait-timeout 300
docker run -d --name "$edge" --network host -v "$work/edge/nginx.conf:/etc/nginx/conf.d/default.conf:ro" \
  -v "$work/edge:/etc/ssl:ro" docker.io/library/nginx:1.29-alpine >/dev/null
for _ in $(seq 30); do
  [ "$(curl -sk --resolve "llm.test:$port:127.0.0.1" -o /dev/null -w '%{http_code}' "https://llm.test:$port/v1/models")" = 401 ] && break
  sleep 1
done

load="$repo/deploy/tests/rolling-load.py"; stop="$work/stop"; pids=()
python3 "$load" llm.test "$port" /v1/chat/completions 401 16 "$stop" POST 127.0.0.1 > "$work/post.txt" 2>&1 & pids+=($!)
python3 "$load" llm.test "$port" /v1/models 401 8 "$stop" GET 127.0.0.1 > "$work/get.txt" 2>&1 & pids+=($!)
python3 "$load" llm-admin.test "$port" / 200 4 "$stop" GET 127.0.0.1 > "$work/admin.txt" 2>&1 & pids+=($!)
sleep 5
COMPOSE_PROJECT_NAME="$project" ./deploy/rolling-update.sh --force
sleep 5
touch "$stop"
failed=0
for pid in "${pids[@]}"; do wait "$pid" || failed=1; done
cat "$work/post.txt" "$work/get.txt" "$work/admin.txt"
if [ "$failed" = 1 ]; then
  echo "error: requests were lost during the rolling update" >&2
  docker logs "$edge" 2>&1 | grep -E '\[(error|warn)\]' | tail -20 >&2 || true
  exit 1
fi
echo "No request lost while every slot was replaced."
