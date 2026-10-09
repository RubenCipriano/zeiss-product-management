#!/usr/bin/env bash
# Runs a k6 scenario inside the cluster (a Kubernetes Job), so the measurement covers gateway + API + data stores
# and not the developer machine's port-forward tunnel.
#
#   load-tests/run.sh smoke | load | stress | spike | stock-consistency | rate-limit
#   RATE=300 load-tests/run.sh load
#   PEAK=2000 load-tests/run.sh stress
#
# Stress and spike runs measure capacity, not throttling: the gateway's per-IP limit (all k6 traffic comes from one
# pod IP) and the API's per-user limits (k6 simulates only 10 users) are raised for the duration of the test and
# restored afterwards. The rate-limit scenario verifies the limits themselves.
set -euo pipefail

SCENARIO="${1:?usage: run.sh <scenario>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=product-management
JOB="k6-$SCENARIO"
K6_IMAGE="${K6_IMAGE:-grafana/k6:2.3.0}"

[[ -f "$HERE/$SCENARIO.js" ]] || { echo "Unknown scenario: $SCENARIO" >&2; exit 1; }

relax_limits=false
if [[ "$SCENARIO" == "stress" || "$SCENARIO" == "spike" ]]; then relax_limits=true; fi

restore() {
  if $relax_limits; then
    echo "==> Restoring rate limits"
    kubectl -n "$NAMESPACE" set env deployment/gateway GATEWAY_RATE_LIMIT_RPS=200 GATEWAY_RATE_LIMIT_BURST=400 >/dev/null
    kubectl -n "$NAMESPACE" set env deployment/api RateLimiting__RequestsPerSecond- RateLimiting__RequestBurst- \
      RateLimiting__WritesPerSecond- RateLimiting__WriteBurst- >/dev/null
  fi
}
trap restore EXIT

if $relax_limits; then
  echo "==> Raising gateway and API rate limits for this run"
  kubectl -n "$NAMESPACE" set env deployment/gateway GATEWAY_RATE_LIMIT_RPS=100000 GATEWAY_RATE_LIMIT_BURST=100000 >/dev/null
  # Container env overrides the ConfigMap values (envFrom) for these keys.
  kubectl -n "$NAMESPACE" set env deployment/api RateLimiting__RequestsPerSecond=100000 RateLimiting__RequestBurst=100000 \
    RateLimiting__WritesPerSecond=100000 RateLimiting__WriteBurst=100000 >/dev/null
  kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=180s >/dev/null
  kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s >/dev/null
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
            - { name: BASE_URL, value: "http://gateway.product-management.svc.cluster.local:8080" }
            - { name: RATE, value: "${RATE:-150}" }
            - { name: PEAK, value: "${PEAK:-800}" }
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
