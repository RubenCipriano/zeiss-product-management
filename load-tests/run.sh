#!/usr/bin/env bash
# Runs a k6 scenario inside the cluster (a Kubernetes Job), so the measurement covers gateway + API + data stores
# and not the developer machine's port-forward tunnel.
#
#   load-tests/run.sh smoke | load | stress | spike | stock-consistency | rate-limit | teams
#   RATE=300 load-tests/run.sh load
#   PEAK=2000 load-tests/run.sh stress
#   RATE=300 load-tests/run.sh teams      # after load-tests/seed-bulk.sh; also verifies the data afterwards
#   PEAK=5000 load-tests/run.sh teams     # capacity staircase up to 5,000 iterations/s, rate limits lifted
#   PEAK=12000 load-tests/run.sh browse   # read capacity with a hot set (edge and Redis caches), limits lifted
#
# Stress and spike runs measure capacity, not throttling: the gateway's per-IP limit (all k6 traffic comes from one
# pod IP) and the API's per-user limits (k6 simulates only 10 users) are raised for the duration of the test and
# restored afterwards. The rate-limit scenario verifies the limits themselves. The teams scenario keeps the API's
# per-consumer limits (they are part of what it tests) and only raises the gateway's per-IP limit: real teams call
# from different addresses, while here they all share the k6 pod's.
set -euo pipefail

SCENARIO="${1:?usage: run.sh <scenario>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=product-management
JOB="k6-$SCENARIO"
K6_IMAGE="${K6_IMAGE:-grafana/k6:2.3.0}"

[[ -f "$HERE/$SCENARIO.js" ]] || { echo "Unknown scenario: $SCENARIO" >&2; exit 1; }

relax_limits=false
relax_gateway=false
if [[ "$SCENARIO" == "stress" || "$SCENARIO" == "spike" ]]; then relax_limits=true; relax_gateway=true; fi
if [[ "$SCENARIO" == "browse" ]]; then relax_limits=true; relax_gateway=true; fi
if [[ "$SCENARIO" == "teams" ]]; then
  relax_gateway=true
  # A capacity run measures the system, not the per-consumer limits that protect it from one abusive client.
  if [[ -n "${PEAK:-}" ]]; then relax_limits=true; fi
fi

sql() {
  kubectl -n "$NAMESPACE" exec postgres-0 -- sh -c 'psql -X -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$1"' psql "$1"
}

restore() {
  if $relax_gateway; then
    kubectl -n "$NAMESPACE" set env deployment/gateway GATEWAY_RATE_LIMIT_RPS=200 GATEWAY_RATE_LIMIT_BURST=400 >/dev/null
  fi
  if $relax_limits; then
    echo "==> Restoring rate limits"
    kubectl -n "$NAMESPACE" set env deployment/api RateLimiting__RequestsPerSecond- RateLimiting__RequestBurst- \
      RateLimiting__WritesPerSecond- RateLimiting__WriteBurst- >/dev/null
  fi
}
trap restore EXIT

if $relax_gateway; then
  echo "==> Raising the gateway's per-IP rate limit for this run"
  kubectl -n "$NAMESPACE" set env deployment/gateway GATEWAY_RATE_LIMIT_RPS=100000 GATEWAY_RATE_LIMIT_BURST=100000 >/dev/null
  kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=180s >/dev/null
fi
if $relax_limits; then
  echo "==> Raising the API's rate limits for this run"
  # Container env overrides the ConfigMap values (envFrom) for these keys.
  kubectl -n "$NAMESPACE" set env deployment/api RateLimiting__RequestsPerSecond=100000 RateLimiting__RequestBurst=100000 \
    RateLimiting__WritesPerSecond=100000 RateLimiting__WriteBurst=100000 >/dev/null
  kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s >/dev/null
fi

id_range=""
if [[ "$SCENARIO" == "teams" ]]; then
  id_range="$(sql "SELECT min(id) || ' ' || max(id) FROM products")"
  ledger_before="$(sql "SELECT count(*) FROM stock_movements WHERE performed_by LIKE 'load-tester-%'")"
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
            - { name: RATE, value: "${RATE:-$([[ "$SCENARIO" == browse ]] || echo 150)}" }
            - { name: PEAK, value: "${PEAK:-$([[ "$SCENARIO" == teams || "$SCENARIO" == browse ]] || echo 800)}" }
            - { name: ID_MIN, value: "${id_range%% *}" }
            - { name: ID_MAX, value: "${id_range##* }" }
          resources:
            requests: { cpu: 500m, memory: 256Mi }
            limits: { cpu: "${K6_CPU:-2}", memory: 2Gi }
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
log="$(mktemp)"
kubectl -n "$NAMESPACE" logs -f job/"$JOB" | tee "$log"

passed=true
if ! kubectl -n "$NAMESPACE" wait --for=condition=complete job/"$JOB" --timeout=10s >/dev/null 2>&1; then
  echo "==> $SCENARIO: a threshold was crossed or the run errored"
  passed=false
fi

if [[ "$SCENARIO" == "teams" ]]; then
  # Every stock change the API applied must have exactly one ledger row: retried copies add none. Changes whose
  # outcome k6 never learned (no answer after its retries) may or may not have applied, so they widen the range.
  applied="$(sed -n 's/^TEAMS_RESULT applied=\([0-9]*\).*/\1/p' "$log")"
  unknown="$(sed -n 's/^TEAMS_RESULT .*unknown=\([0-9]*\).*/\1/p' "$log")"
  ledger_after="$(sql "SELECT count(*) FROM stock_movements WHERE performed_by LIKE 'load-tester-%'")"
  added=$((ledger_after - ledger_before))
  echo "==> Stock changes applied (k6): ${applied:-?}, outcome unknown: ${unknown:-?}; ledger rows added: $added"
  if [[ -z "$applied" || "$added" -lt "$applied" || "$added" -gt $((applied + ${unknown:-0})) ]]; then
    echo "==> MISMATCH: a change was applied twice or lost"
    passed=false
  else
    echo "==> Every applied change has exactly one ledger row: no duplicates, nothing lost"
  fi
  "$HERE/verify-data.sh" || passed=false
fi
rm -f "$log"

if $passed; then
  echo "==> $SCENARIO: PASSED"
else
  echo "==> $SCENARIO: FAILED"
  exit 1
fi
