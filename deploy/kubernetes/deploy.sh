#!/usr/bin/env bash
# Deploys or updates an overlay without downtime (docs/kubernetes.md): applies configuration and data stores, runs the
# migrations Job and waits for it, then rolls out the gateway, admin API and Data API one pod at a time.
#
#   ./deploy/kubernetes/deploy.sh deploy/kubernetes/my-overlay
#
# Only for releases the upgrade guide marks "Rolling update: yes": old pods keep running against the new schema until
# they are replaced. Needs kubectl 1.27 or later.
set -euo pipefail
overlay="${1:?usage: deploy.sh OVERLAY_DIR}"
apps="gateway adminapi dataapi"
manifest="$(mktemp)"; trap 'rm -f "$manifest"' EXIT
kubectl kustomize "$overlay" > "$manifest"
# The overlay's namespace, from the migrations Job (empty: the context's current namespace).
ns="$(awk '/^---/{job=0} /^kind: Job$/{job=1} job && /^  namespace: /{print $2; exit}' "$manifest")"
k=(kubectl ${ns:+-n "$ns"})

echo "==> Configuration and data stores"
kubectl apply -f "$manifest" -l 'app.kubernetes.io/component notin (gateway,adminapi,dataapi,migrations)'
if "${k[@]}" get statefulset postgres >/dev/null 2>&1; then "${k[@]}" rollout status statefulset/postgres --timeout=10m; fi
if "${k[@]}" get deployment redis >/dev/null 2>&1; then "${k[@]}" rollout status deployment/redis --timeout=5m; fi

echo "==> Migrations"
# A Job's pod template is immutable, and it must run again for every release.
"${k[@]}" delete job migrations --ignore-not-found --wait
kubectl apply -f "$manifest" -l app.kubernetes.io/component=migrations
while :; do
  state="$("${k[@]}" get job migrations -o jsonpath='{range .status.conditions[?(@.status=="True")]}{.type} {end}')"
  case " $state " in
    *" Complete "*) break;;
    *" Failed "*) echo "error: migrations failed" >&2; "${k[@]}" logs job/migrations --tail=50 >&2 || true; exit 1;;
  esac
  sleep 3
done

echo "==> Applications"
kubectl apply -f "$manifest"
for app in $apps; do
  if "${k[@]}" get deployment "$app" >/dev/null 2>&1; then "${k[@]}" rollout status "deployment/$app" --timeout=15m; fi
done
echo "==> Done"
