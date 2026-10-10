#!/usr/bin/env bash
# Starts, stops and shows the shared infrastructure in the current kubectl context: PostgreSQL, Redis and the logs
# (Loki, Grafana, Alloy), all in the namespace `infra`, for every service on the cluster.
#
#   ./infra.sh up                          # everything: postgres, redis, logs
#   ./infra.sh up postgres redis           # only these two (no logs)
#   ./infra.sh down logs                   # remove a component; its data (volumes) and passwords are kept
#   ./infra.sh down postgres --delete-data # ... and delete its volumes and password too (asks no questions)
#   ./infra.sh status                      # pods, services, volumes
#
#   postgres   PostgreSQL 18, postgres.infra.svc.cluster.local:5432; nightly pg_dump      postgres.yaml
#   redis      Redis 8, redis.infra.svc.cluster.local:6379, password protected              redis.yaml
#   logs       Loki (14 days), Grafana (dashboards/, 12 alert rules), Alloy collecting      logs.yaml, dashboards/
#              every pod's logs and the browser's error reports (Service alloy-faro:12347)
#
# `up` also creates what must never be committed, once: the namespace and the random passwords (Secrets
# postgres-admin and redis-auth), and installs metrics-server when the cluster has none (the autoscalers need it).
# A service then gets its database with ./create-database.sh, and its logs without doing anything.
#
# Another Loki instead of the one in the cluster (e.g. Grafana Cloud): Loki and Grafana are not deployed (Loki's
# volume is kept) and Alloy pushes there.
#   LOKI_URL=https://<host>/loki/api/v1/push LOKI_USERNAME=<user> LOKI_PASSWORD=<token> ./infra.sh up logs
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=infra
METRICS_SERVER_VERSION=v0.8.0
ALL=(postgres redis logs)

usage() {
  echo "usage: $0 up|down|status [postgres] [redis] [logs] [--delete-data]" >&2
  exit 1
}

# Indents what kubectl prints, under the step that printed it.
indent() {
  sed 's/^/    /'
}

# A random password: 48 hex characters from the kernel's generator (no openssl needed).
random_password() {
  od -An -tx1 -N24 /dev/urandom | tr -d ' \n'
}

ensure_namespace() {
  kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml \
    | kubectl label --local -f - app.kubernetes.io/part-of=infra -o yaml \
    | kubectl apply -f - >/dev/null
}

# Creates the Secret <name> with a random `password` if it does not exist ($created_secret says whether it did);
# never changes an existing one (PostgreSQL stores its password when it first creates its data directory).
ensure_password_secret() {
  local name="$1" component="$2"
  created_secret=false
  if kubectl -n "$NAMESPACE" get secret "$name" >/dev/null 2>&1; then
    return
  fi
  created_secret=true
  echo "==> Secret $name: a new random password"
  kubectl -n "$NAMESPACE" create secret generic "$name" --from-literal=password="$(random_password)" \
    --dry-run=client -o yaml \
    | kubectl label --local -f - -o yaml \
        app.kubernetes.io/component="$component" app.kubernetes.io/part-of=infra \
    | kubectl apply -f - >/dev/null
}

# kubectl apply, printing what changed; returns (in $changed_config) whether a ConfigMap changed, because pods read
# their configuration files when they start.
apply() {
  local output
  output="$(kubectl apply "$@")"
  echo "$output" | indent
  changed_config=false
  if grep -q '^configmap/.* configured' <<<"$output"; then
    changed_config=true
  fi
}

# metrics-server reports pod CPU and memory: the API's and the gateway's HorizontalPodAutoscalers need it, and
# `kubectl top` uses it. k3s and most managed clusters ship it; Docker Desktop does not.
ensure_metrics_server() {
  if kubectl get apiservice v1beta1.metrics.k8s.io >/dev/null 2>&1; then
    return
  fi
  echo "==> metrics-server $METRICS_SERVER_VERSION (the cluster has none)"
  kubectl apply -f "https://github.com/kubernetes-sigs/metrics-server/releases/download/$METRICS_SERVER_VERSION/components.yaml"
  if [[ "$(kubectl config current-context)" == docker-desktop ]]; then
    # Docker Desktop's kubelet serves a self-signed certificate that does not name the node's IP. Local clusters
    # only: on a real cluster the kubelet certificates are signed by the cluster CA.
    kubectl -n kube-system patch deployment metrics-server --type=json \
      -p '[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
  fi
  kubectl -n kube-system rollout status deployment/metrics-server --timeout=180s
}

up_postgres() {
  echo "==> postgres: PostgreSQL 18 (postgres.yaml)"
  ensure_password_secret postgres-admin postgres
  apply -f "$HERE/postgres.yaml"
  if $changed_config; then
    echo "    postgresql.conf changed. Most settings only need a reload:"
    echo "      kubectl -n $NAMESPACE exec statefulset/postgres -- psql -U postgres -c 'SELECT pg_reload_conf()'"
    echo "    shared_buffers, max_connections and shared_preload_libraries need a restart:"
    echo "      kubectl -n $NAMESPACE rollout restart statefulset/postgres"
  fi
  kubectl -n "$NAMESPACE" rollout status statefulset/postgres --timeout=900s
}

up_redis() {
  echo "==> redis: Redis 8 (redis.yaml)"
  ensure_password_secret redis-auth redis
  local running=false
  if kubectl -n "$NAMESPACE" get deployment redis >/dev/null 2>&1; then running=true; fi
  apply -f "$HERE/redis.yaml"
  # A new password (the Secret was deleted to rotate it) or a changed redis.conf: Redis reads both when it starts.
  # The services get the new password on their next deploy (the backend's deploy.sh copies it).
  if $changed_config || { $running && $created_secret; }; then
    kubectl -n "$NAMESPACE" rollout restart deployment/redis
  fi
  kubectl -n "$NAMESPACE" rollout status deployment/redis --timeout=300s
}

up_logs() {
  local restart_alloy=false
  if [[ -n "${LOKI_URL:-}" ]]; then
    echo "==> logs: Alloy pushes to $LOKI_URL (no Loki or Grafana in the cluster)"
    local output
    output="$(kubectl -n "$NAMESPACE" create secret generic loki-external \
        --from-literal=url="$LOKI_URL" \
        --from-literal=username="${LOKI_USERNAME:-}" --from-literal=password="${LOKI_PASSWORD:-}" \
        --dry-run=client -o yaml \
      | kubectl label --local -f - -o yaml app.kubernetes.io/component=logs app.kubernetes.io/part-of=infra \
      | kubectl apply -f -)"
    [[ "$output" == *" unchanged" ]] || restart_alloy=true
    # Loki and Grafana are not needed; Loki's volume (data-loki-0) is kept for when this is switched back.
    kubectl -n "$NAMESPACE" delete statefulset,deployment,service,configmap \
      -l 'app.kubernetes.io/name in (loki,grafana)' --ignore-not-found
    apply -f "$HERE/logs.yaml" -l app.kubernetes.io/name=alloy
    if $changed_config; then restart_alloy=true; fi
  else
    echo "==> logs: Loki, Grafana and Alloy (logs.yaml, dashboards/)"
    if kubectl -n "$NAMESPACE" get secret loki-external >/dev/null 2>&1; then
      kubectl -n "$NAMESPACE" delete secret loki-external
      restart_alloy=true
    fi
    # The dashboards are JSON files of their own, so they stay easy to edit and diff: one ConfigMap made from the
    # folder (each file a key).
    kubectl -n "$NAMESPACE" create configmap grafana-dashboards --from-file="$HERE/dashboards/" \
      --dry-run=client -o yaml \
      | kubectl label --local -f - -o yaml \
          app.kubernetes.io/name=grafana app.kubernetes.io/component=logs app.kubernetes.io/part-of=infra \
      | kubectl apply -f - | indent
    apply -f "$HERE/logs.yaml"
    if $changed_config; then
      # Which of the three read a changed file is not worth working out: they restart in seconds.
      kubectl -n "$NAMESPACE" rollout restart statefulset/loki deployment/grafana
      restart_alloy=true
    fi
    kubectl -n "$NAMESPACE" rollout status statefulset/loki --timeout=300s
    kubectl -n "$NAMESPACE" rollout status deployment/grafana --timeout=300s
  fi
  if $restart_alloy; then
    kubectl -n "$NAMESPACE" rollout restart deployment/alloy
  fi
  kubectl -n "$NAMESPACE" rollout status deployment/alloy --timeout=300s
}

# Removes a component's workloads, Services and configuration. Its volumes (PersistentVolumeClaims) and its password
# Secret stay unless --delete-data is given, so `up` brings it back with its data.
down_component() {
  local component="$1"
  echo "==> $component: removing"
  kubectl -n "$NAMESPACE" delete statefulset,deployment,cronjob,job,service,configmap,serviceaccount,networkpolicy \
    -l app.kubernetes.io/component="$component" --ignore-not-found
  kubectl delete clusterrole,clusterrolebinding -l app.kubernetes.io/component="$component" --ignore-not-found
  if $delete_data; then
    echo "    --delete-data: deleting its volumes and Secrets"
    kubectl -n "$NAMESPACE" delete pvc,secret -l app.kubernetes.io/component="$component" --ignore-not-found --wait=false
    if [[ "$component" == postgres ]]; then
      echo "    The services' database-credentials Secrets now name users that no longer exist: run"
      echo "    create-database.sh again for each service after the next ./infra.sh up postgres."
    fi
  else
    local kept
    kept="$(kubectl -n "$NAMESPACE" get pvc,secret -l app.kubernetes.io/component="$component" -o name 2>/dev/null)"
    if [[ -n "$kept" ]]; then
      echo "    kept (delete them with --delete-data):"
      echo "$kept" | indent | indent
    fi
  fi
}

status() {
  echo "Context: $(kubectl config current-context)"
  kubectl -n "$NAMESPACE" get pods,services,pvc -o wide 2>/dev/null || echo "No namespace $NAMESPACE yet: ./infra.sh up"
  echo
  echo "From this machine:"
  echo "  Grafana     kubectl -n $NAMESPACE port-forward svc/grafana 3000:3000     → http://localhost:3000"
  echo "  Loki        kubectl -n $NAMESPACE port-forward svc/loki 3100:3100        → http://localhost:3100"
  echo "  PostgreSQL  kubectl -n $NAMESPACE exec -it statefulset/postgres -- psql -U postgres"
  echo "  Redis       kubectl -n $NAMESPACE exec -it deployment/redis -- redis-cli"
}

command="${1:-}"
[[ -n "$command" ]] || usage
shift
components=()
delete_data=false
for arg in "$@"; do
  case "$arg" in
    postgres | redis | logs) components+=("$arg") ;;
    --delete-data) delete_data=true ;;
    *) echo "Unknown argument: $arg" >&2; usage ;;
  esac
done
if [[ ${#components[@]} == 0 ]]; then
  components=("${ALL[@]}")
fi
if $delete_data && [[ "$command" != down ]]; then
  echo "--delete-data only goes with down" >&2
  exit 1
fi

case "$command" in
  up)
    echo "Kubernetes context: $(kubectl config current-context)"
    ensure_namespace
    ensure_metrics_server
    for component in "${components[@]}"; do
      "up_$component"
    done
    echo
    status
    ;;
  down)
    for component in "${components[@]}"; do
      down_component "$component"
    done
    ;;
  status) status ;;
  *) usage ;;
esac
