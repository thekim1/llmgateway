#!/usr/bin/env bash
# End-to-end test of the Kubernetes manifests (docs/kubernetes.md) on a kind cluster: secrets from init-deployment.sh,
# deploy.sh with the in-cluster data stores and the Data API under the "restricted" Pod Security Standard, then a rolling
# restart of the gateway and admin API while a pod in the cluster sends requests to their Services. Fails if a single
# request is lost.
#
#   deploy/tests/kubernetes-e2e.sh           # needs kind, kubectl and the local images ume-llm-gateway/*:<version>
#   KEEP_CLUSTER=1 deploy/tests/kubernetes-e2e.sh    # leave the cluster running afterwards
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cluster="${KIND_CLUSTER:-ume-e2e}"; ns=ume-e2e
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props")"
work="$(mktemp -d)"
cp -R "$repo/deploy" "$work/"
cd "$work"
cleanup() {
  [ -n "${KEEP_CLUSTER:-}" ] || kind delete cluster --name "$cluster" >/dev/null 2>&1 || true
  cd / && rm -r -f "$work"
}
trap cleanup EXIT

kind get clusters 2>/dev/null | grep -qx "$cluster" || kind create cluster --name "$cluster" --wait 120s
kubectl config use-context "kind-$cluster" >/dev/null
for svc in gateway adminapi migrations dataapi; do kind load docker-image --name "$cluster" "ume-llm-gateway/$svc:$version"; done

./deploy/init-deployment.sh --no-chown --yes --oidc-authority https://login.microsoftonline.com/common/v2.0 \
  --gateway-host llm.test --admin-host llm-admin.test >/dev/null
kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl label namespace "$ns" pod-security.kubernetes.io/enforce=restricted --overwrite >/dev/null
./deploy/kubernetes/create-secrets.sh --namespace "$ns" --datastores
mkdir -p deploy/kubernetes/e2e
cat > deploy/kubernetes/e2e/kustomization.yaml <<YAML
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: $ns
resources: [../base]
components: [../components/datastores, ../components/dataapi]
configMapGenerator:
  - name: ume-adminapi-config
    behavior: merge
    literals: [Oidc__Authority=https://login.microsoftonline.com/common/v2.0, Gateway__BaseUrl=https://llm.test]
  - name: ume-dataapi-config
    behavior: merge
    literals: [DataApi__Authority=https://login.microsoftonline.com/common/v2.0]
YAML
./deploy/kubernetes/deploy.sh deploy/kubernetes/e2e

kubectl -n "$ns" create configmap rolling-load --from-file=rolling-load.py="$repo/deploy/tests/rolling-load.py" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl -n "$ns" apply -f - >/dev/null <<YAML
apiVersion: v1
kind: Pod
metadata: {name: rolling-load}
spec:
  restartPolicy: Never
  securityContext: {runAsNonRoot: true, runAsUser: 1000, seccompProfile: {type: RuntimeDefault}}
  containers:
    - name: load
      image: docker.io/library/python:3.13-alpine
      command: [sh, -c, "sleep infinity"]
      securityContext: {allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}}
      volumeMounts: [{name: load, mountPath: /load}]
  volumes: [{name: load, configMap: {name: rolling-load}}]
YAML
kubectl -n "$ns" wait --for=condition=Ready pod/rolling-load --timeout=300s
# Each leg writes its output and exit code; the pod keeps running so they can be read afterwards.
kubectl -n "$ns" exec rolling-load -- sh -c '
  (python /load/rolling-load.py gateway 8443 /v1/chat/completions 401,429 16 /tmp/stop POST > /tmp/post.txt 2>&1; echo $? > /tmp/post.rc) </dev/null >/dev/null 2>&1 &
  (python /load/rolling-load.py gateway 8443 /v1/models 401,429 8 /tmp/stop > /tmp/get.txt 2>&1; echo $? > /tmp/get.rc) </dev/null >/dev/null 2>&1 &
  (python /load/rolling-load.py adminapi 8443 / 200 4 /tmp/stop > /tmp/admin.txt 2>&1; echo $? > /tmp/admin.rc) </dev/null >/dev/null 2>&1 &
  sleep 1'
sleep 8
kubectl -n "$ns" rollout restart deployment/gateway deployment/adminapi
kubectl -n "$ns" rollout status deployment/gateway --timeout=15m
kubectl -n "$ns" rollout status deployment/adminapi --timeout=15m
sleep 15
kubectl -n "$ns" exec rolling-load -- touch /tmp/stop
for _ in $(seq 60); do
  kubectl -n "$ns" exec rolling-load -- sh -c 'test -f /tmp/post.rc && test -f /tmp/get.rc && test -f /tmp/admin.rc' && break
  sleep 1
done
failed=0
for leg in post get admin; do
  kubectl -n "$ns" exec rolling-load -- cat "/tmp/$leg.txt"
  [ "$(kubectl -n "$ns" exec rolling-load -- cat "/tmp/$leg.rc" 2>/dev/null)" = 0 ] || failed=1
done
[ "$failed" = 0 ] || { echo "error: requests were lost during the rolling restart" >&2; exit 1; }
echo "No request lost during the rolling restart."
