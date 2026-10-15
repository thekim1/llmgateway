#!/usr/bin/env bash
# Creates (or updates) the Kubernetes secrets from the certificates and secrets that deploy/init-deployment.sh
# generated, so Kubernetes and Compose installations are set up the same way (docs/kubernetes.md).
#
#   ./deploy/init-deployment.sh --no-chown --yes --oidc-authority https://idp.example.se/realms/ume \
#       --gateway-host llm.example.se --admin-host llm-admin.example.se
#   ./deploy/kubernetes/create-secrets.sh --namespace ume-gateway [--datastores]
#
#   --namespace NS     namespace of the overlay (created if missing)
#   --datastores       also create the secrets for components/datastores (in-cluster Postgres and Redis)
#   --certs-dir DIR / --secrets-dir DIR   if init-deployment.sh wrote them elsewhere
#
# Secrets: ume-<service>-secrets (files the app reads as settings, e.g. ConnectionStrings__gatewaydb) and
# ume-<service>-certs (TLS certificate, internal CA, Data Protection certificate) for gateway, adminapi, migrations
# and dataapi. The connection strings point at the hosts "postgres" and "redis"; with your own databases, edit
# deploy/secrets/<service>/ConnectionStrings__* first.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/../certs"; secrets="$here/../secrets"; ns=""; datastores=0
while [ $# -gt 0 ]; do
  case "$1" in
    --namespace|-n) ns="$2"; shift 2;;
    --datastores) datastores=1; shift;;
    --certs-dir) certs="$2"; shift 2;;
    --secrets-dir) secrets="$2"; shift 2;;
    -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done
[ -n "$ns" ] || { echo "error: --namespace is required" >&2; exit 1; }
[ -r "$secrets/gateway/ConnectionStrings__gatewaydb" ] || {
  echo "error: $secrets/gateway is missing or unreadable. Run deploy/init-deployment.sh --no-chown first." >&2; exit 1; }
kubectl get namespace "$ns" >/dev/null 2>&1 || kubectl create namespace "$ns"

secret() { # secret NAME DIR — every regular file in DIR becomes a key
  local args=() f
  for f in "$2"/*; do [ -f "$f" ] && args+=("--from-file=$f"); done
  kubectl -n "$ns" create secret generic "$1" "${args[@]}" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  echo "secret/$1"
}
for svc in gateway adminapi migrations dataapi; do
  secret "ume-$svc-secrets" "$secrets/$svc"
  secret "ume-$svc-certs" "$certs/$svc"
done
if [ "$datastores" = 1 ]; then
  for svc in postgres redis; do
    secret "ume-$svc-secrets" "$secrets/$svc"
    secret "ume-$svc-certs" "$certs/$svc"
  done
fi
echo "Back up $secrets/.pepper and $certs/data-protection.pfx outside the cluster: without them a restored database is unusable."
