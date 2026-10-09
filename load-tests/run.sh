#!/usr/bin/env bash
# Runs a k6 scenario inside the cluster (a Kubernetes Job), so the measurement covers gateway + API + data stores
# and not the developer machine's port-forward tunnel.
#
#   load-tests/run.sh smoke | load | stress | spike | stock-consistency | rate-limit
#   RATE=300 load-tests/run.sh load
#
# For stress and spike runs the gateway's per-IP limit is raised for the duration of the test (all k6 traffic
# comes from one pod IP, unlike real users), then restored.
set -euo pipefail

SCENARIO="${1:?usage: run.sh <scenario>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
export KUBECONFIG="${KUBECONFIG:-$ROOT/.kube/microk8s}"
NAMESPACE=product-management
JOB="k6-$SCENARIO"
K6_IMAGE="${K6_IMAGE:-grafana/k6:2.3.0}"

[[ -f "$HERE/$SCENARIO.js" ]] || { echo "Unknown scenario: $SCENARIO" >&2; exit 1; }

relax_limits=false
if [[ "$SCENARIO" == "stress" || "$SCENARIO" == "spike" ]]; then relax_limits=true; fi

restore() {
  if $relax_limits; then
    echo "==> Restoring gateway rate limit"
    kubectl -n "$NAMESPACE" set env deployment/gateway GATEWAY_RATE_LIMIT_RPS=200 GATEWAY_RATE_LIMIT_BURST=400 >/dev/null
  fi
}
trap restore EXIT

if $relax_limits; then
  echo "==> Raising the gateway's per-IP limit for this run"
  kubectl -n "$NAMESPACE" set env deployment/gateway GATEWAY_RATE_LIMIT_RPS=10000 GATEWAY_RATE_LIMIT_BURST=10000 >/dev/null
  kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=120s >/dev/null
fi

echo "==> Uploading scripts"
kubectl -n "$NAMESPACE" create configmap k6-scripts \
  --from-file="$HERE" --dry-run=client -o yaml \
  | kubectl apply -f - >/dev/null
# ConfigMaps cannot hold directories: lib/*.js are mounted next to the scenarios and imported as ./lib/...
kubectl -n "$NAMESPACE" create configmap k6-lib --from-file="$HERE/lib" --dry-run=client -o yaml | kubectl apply -f - >/dev/null

kubectl -n "$NAMESPACE" delete job "$JOB" --ignore-not-found >/dev/null

cat <<YAML | kubectl apply -f - >/dev/null
apiVersion: batch/v1
kind: Job
metadata:
  name: $JOB
  namespace: $NAMESPACE
  labels: { app.kubernetes.io/name: load-test }
spec:
  backoffLimit: 0
  ttlSecondsAfterFinished: 600
  template:
    metadata:
      labels: { app.kubernetes.io/name: load-test }
    spec:
      restartPolicy: Never
      containers:
        - name: k6
          image: $K6_IMAGE
          args: ["run", "--quiet", "/scripts/$SCENARIO.js"]
          env:
            - { name: BASE_URL, value: "http://gateway.product-management.svc.cluster.local" }
            - { name: RATE, value: "${RATE:-150}" }
          resources:
            requests: { cpu: 500m, memory: 256Mi }
            limits: { cpu: "2", memory: 1Gi }
          volumeMounts:
            - { name: scripts, mountPath: /scripts }
            - { name: lib, mountPath: /scripts/lib }
      volumes:
        - name: scripts
          configMap: { name: k6-scripts }
        - name: lib
          configMap: { name: k6-lib }
YAML

echo "==> Running $SCENARIO (logs follow)"
kubectl -n "$NAMESPACE" wait --for=condition=ready pod -l job-name="$JOB" --timeout=180s >/dev/null 2>&1 || true
kubectl -n "$NAMESPACE" logs -f job/"$JOB"

if kubectl -n "$NAMESPACE" wait --for=condition=complete job/"$JOB" --timeout=10s >/dev/null 2>&1; then
  echo "==> $SCENARIO: PASSED (all thresholds met)"
else
  echo "==> $SCENARIO: FAILED (a threshold was crossed or the run errored)"
  exit 1
fi
