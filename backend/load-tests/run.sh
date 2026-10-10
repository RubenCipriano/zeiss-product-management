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
# The virtual users sign in as the three demo accounts (viewer, clerk, manager), so many of them share one account and
# the API's per-user limits (50 requests/s, 10 writes/s) would cap the throughput. For every scenario except
# rate-limit, the API's limits are raised for the duration of the run and always restored afterwards (the values in
# the Deployment are saved first and set back at the end): the tests measure what the system handles with a few
# accounts, they no longer simulate many distinct users. rate-limit runs against the real limits, which it verifies.
# Stress, spike, browse and teams also raise the gateway's per-IP limit: all k6 traffic comes from one pod IP.
set -euo pipefail

SCENARIO="${1:?usage: run.sh <scenario>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=product-management
JOB="k6-$SCENARIO"
K6_IMAGE="${K6_IMAGE:-grafana/k6:2.3.0}"

[[ -f "$HERE/$SCENARIO.js" ]] || { echo "Unknown scenario: $SCENARIO" >&2; exit 1; }

relax_limits=true
relax_gateway=false
if [[ "$SCENARIO" == "rate-limit" ]]; then relax_limits=false; fi
case "$SCENARIO" in stress | spike | browse | teams) relax_gateway=true ;; esac

# PostgreSQL: by default the shared server in the cluster (namespace infra), through `kubectl exec` as its
# administrator. Set PSQL to the psql command of another server (e.g. PSQL='psql -h db.internal -U postgres -d
# product_management'), or PG_USER / PG_DATABASE for another user or database.
if [[ -n "${PSQL:-}" ]]; then read -r -a psql_cmd <<< "$PSQL"
else psql_cmd=(kubectl -n infra exec -i statefulset/postgres -- psql -U "${PG_USER:-postgres}" -d "${PG_DATABASE:-product_management}"); fi

sql() {
  "${psql_cmd[@]}" -X -At -c "$1"
}

# The current value of an environment variable of a Deployment's container (as applied by its deploy.sh).
env_value() {
  kubectl -n "$NAMESPACE" get deployment "$1" \
    -o jsonpath="{.spec.template.spec.containers[0].env[?(@.name==\"$2\")].value}"
}

# The limits as deployed, to put them back afterwards.
saved_api_limits=(
  "RateLimiting__RequestsPerSecond=$(env_value api RateLimiting__RequestsPerSecond)"
  "RateLimiting__RequestBurst=$(env_value api RateLimiting__RequestBurst)"
  "RateLimiting__WritesPerSecond=$(env_value api RateLimiting__WritesPerSecond)"
  "RateLimiting__WriteBurst=$(env_value api RateLimiting__WriteBurst)"
)
saved_gateway_limits=(
  "GATEWAY_RATE_LIMIT_RPS=$(env_value gateway GATEWAY_RATE_LIMIT_RPS)"
  "GATEWAY_RATE_LIMIT_BURST=$(env_value gateway GATEWAY_RATE_LIMIT_BURST)"
)

restore() {
  if $relax_gateway; then
    kubectl -n "$NAMESPACE" set env deployment/gateway "${saved_gateway_limits[@]}" >/dev/null
  fi
  if $relax_limits; then
    echo "==> Restoring the API's rate limits (${saved_api_limits[*]})"
    kubectl -n "$NAMESPACE" set env deployment/api "${saved_api_limits[@]}" >/dev/null
    kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s >/dev/null
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
  kubectl -n "$NAMESPACE" set env deployment/api RateLimiting__RequestsPerSecond=100000 RateLimiting__RequestBurst=100000 \
    RateLimiting__WritesPerSecond=100000 RateLimiting__WriteBurst=100000 >/dev/null
  kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s >/dev/null
fi

id_range=""
if [[ "$SCENARIO" == "teams" ]]; then
  id_range="$(sql "SELECT min(id) || ' ' || max(id) FROM products")"
  # The ledger only grows (ids from an identity), so the run's rows are the ones above today's highest id: an index
  # range instead of scanning tens of millions of rows for the testers' names.
  ledger_start="$(sql "SELECT coalesce(max(id), 0) FROM stock_movements")"
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
  added="$(sql "SELECT count(*) FROM stock_movements WHERE id > $ledger_start AND performed_by IN ('clerk', 'manager')")"
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
