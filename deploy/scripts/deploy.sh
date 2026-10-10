#!/usr/bin/env bash
# Deploys one environment of the Product Management system to its Kubernetes cluster.
#
#   scripts/deploy.sh <local|staging|production> [infra|apps|all]        (default: apps)
#
#   infra   the shared infrastructure in the namespace infra (PostgreSQL, Redis, logs): scripts/infra.sh up
#   apps    the API, the web app and the gateway in the namespace product-management: environments/<environment>
#   all     infra, then apps
#
# local (a cluster on this machine: Docker Desktop's Kubernetes, context docker-desktop, on macOS or Windows; or a
# single-node k3s on Linux, context default, set up by scripts/setup-ubuntu.sh):
#   1. creates the API's local secrets if they are missing (scripts/local-secrets.sh);
#   2. builds the api, web and gateway images with Docker from the sibling checkouts ../backend and ../frontend (the
#      layout you get by cloning the three repositories side by side) and records their tags in
#      environments/local/generated/build (not committed), so `kubectl diff -k environments/local` compares against
#      exactly what was applied. Docker Desktop's Kubernetes runs the images of its Docker engine as they are; k3s has
#      a containerd of its own, so they are copied into it (docker save | sudo k3s ctr images import);
#   3. creates the service's database if it does not exist yet (scripts/create-database.sh);
#   4. copies the shared Redis' password into the namespace (Secret redis-credentials);
#   5. applies the database migrations with the new API image (scripts/migrate.sh local);
#   6. applies environments/local and waits for the rollouts.
#
# staging, production (this repository's pipeline, or a laptop whose kubectl reaches that cluster):
#   1. refuses an environment whose kustomization.yaml still names no version for an image (newTag: unreleased);
#   2. creates the registry credential (Secret gitlab-registry) and the API's own Secret (api-secrets) from the CI/CD
#      variables below, when they are set (otherwise the existing Secrets are kept);
#   3. creates the service's database and users on the first deploy (scripts/create-database.sh);
#   4. copies the shared Redis' password into the namespace (Secret redis-credentials);
#   5. applies the database migrations with the API image the environment names (scripts/migrate.sh <environment>);
#   6. applies environments/<environment> exactly as committed and waits for the rollouts.
#   Which versions run is chosen in environments/<environment>/kustomization.yaml (scripts/set-version.sh).
#
# Variables:
#   KUBE_CONTEXT                          the kubectl context to use (the GitLab agent: <project path>:<agent name>);
#                                         default: the current context (e.g. of a KUBECONFIG file)
#   BACKEND_DIR, FRONTEND_DIR             local: the checkouts to build (default: ../backend and ../frontend)
#   LOCAL_CLUSTER=docker-desktop|k3s      local: how the images reach the cluster, when the detection gets it wrong
#                                         (default: docker-desktop for that context, k3s for a k3s node on this machine)
#   LOGS=off                              local: no log collector, the gateway answers /collect with 204
#   SEED_SAMPLE_DATA                      sample catalogue on an empty database (default: true for local and staging,
#                                         false for production; scripts/migrate.sh)
#   CI_DEPLOY_USER, CI_DEPLOY_PASSWORD    staging/production: a deploy token with read_registry on both registries
#                                         (GitLab passes the group deploy token named gitlab-deploy-token by itself)
#   CI_REGISTRY                           the registry of that token (default: registry.gitlab.com)
#   API_SIGNING_KEY, API_CONSUMERS        staging/production: files with the ES256 private key (PEM) and consumers.json
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
NAMESPACE=product-management

usage() {
  echo "usage: $0 <local|staging|production> [infra|apps|all]" >&2
  exit 1
}

ENVIRONMENT="${1:-}"
TARGET="${2:-apps}"
case "$ENVIRONMENT" in local | staging | production) ;; *) usage ;; esac
case "$TARGET" in infra | apps | all) ;; *) usage ;; esac
ENV_DIR="$ROOT/environments/$ENVIRONMENT"

# ─── the cluster ──────────────────────────────────────────────────────────────────────────────────────────────────

if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT" >/dev/null
fi
if ! CONTEXT="$(kubectl config current-context 2>/dev/null)"; then
  echo "No Kubernetes context: set KUBE_CONTEXT (GitLab agent) or KUBECONFIG (a kubeconfig file)" >&2
  exit 1
fi
# The laptop's cluster is never staging or production.
if [[ "$ENVIRONMENT" != local && "$CONTEXT" == docker-desktop ]]; then
  echo "$ENVIRONMENT runs on its own cluster, not docker-desktop: set KUBE_CONTEXT or KUBECONFIG" >&2
  exit 1
fi

# local: the images are built on this machine, so the cluster must be able to run them without a registry.
#   docker-desktop   Docker Desktop's Kubernetes runs the images of its own Docker engine (macOS, Windows)
#   k3s              a k3s node on this machine (Linux): the images are copied into k3s' containerd
local_cluster() {
  local versions nodes
  if [[ -n "${LOCAL_CLUSTER:-}" ]]; then
    echo "$LOCAL_CLUSTER"
  elif [[ "$CONTEXT" == docker-desktop ]]; then
    echo docker-desktop
  else
    # k3s reports itself in the kubelet version (v1.34.1+k3s1) and names the node after the machine.
    versions="$(kubectl get nodes -o jsonpath='{.items[*].status.nodeInfo.kubeletVersion}' 2>/dev/null || true)"
    nodes=" $(kubectl get nodes -o jsonpath='{.items[*].metadata.name}' 2>/dev/null || true) "
    if [[ "$versions" == *+k3s* && "$nodes" == *" $(hostname | tr '[:upper:]' '[:lower:]') "* ]] \
      && command -v k3s >/dev/null; then
      echo k3s
    else
      echo other
    fi
  fi
}

if [[ "$ENVIRONMENT" == local && ( "$TARGET" == apps || "$TARGET" == all ) ]]; then
  CLUSTER="$(local_cluster)"
  case "$CLUSTER" in
    docker-desktop | k3s) ;;
    *)
      echo "local builds the images on this machine, and the context $CONTEXT cannot run them without a registry." >&2
      echo "Use Docker Desktop's Kubernetes (context docker-desktop) or a k3s server on this machine" >&2
      echo "(scripts/setup-ubuntu.sh), or set LOCAL_CLUSTER=docker-desktop|k3s if the detection is wrong." >&2
      echo "Any other cluster runs registry images: release them (the app pipelines) and deploy staging or production." >&2
      exit 1
      ;;
  esac
  echo "Deploying $ENVIRONMENT ($TARGET) to Kubernetes context: $CONTEXT ($CLUSTER)"
else
  echo "Deploying $ENVIRONMENT ($TARGET) to Kubernetes context: $CONTEXT"
fi

# ─── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

# kubectl apply of a Secret rendered by `kubectl create --dry-run`, labelled; prints "changed" when it existed and
# its content changed (pods read Secrets when they start, so the caller restarts them).
apply_secret() {
  local output
  output="$(kubectl label --local -f - -o yaml \
      app.kubernetes.io/part-of=product-management app.kubernetes.io/managed-by=deploy.sh \
    | kubectl apply -f -)"
  echo "    $output" >&2
  if [[ "$output" == *" configured"* ]]; then
    echo changed
  fi
}

# The commit of a checkout, plus a timestamp when it has uncommitted changes: a unique, immutable tag per build
# (a folder that is not a git checkout gets dev-<timestamp>).
image_tag() {
  local dir="$1" tag
  tag="$(git -C "$dir" rev-parse --short HEAD 2>/dev/null || echo "dev-$(date +%Y%m%d%H%M%S)")"
  if [[ -n "$(git -C "$dir" status --porcelain -- . 2>/dev/null)" ]]; then
    tag="$tag-dirty-$(date +%Y%m%d%H%M%S)"
  fi
  echo "$tag"
}

# The shared Redis' password, copied from infra/redis-auth into this namespace (a pod reads only its own namespace's
# Secrets). Sets restart_api when it changed, so the API is restarted to read it.
copy_redis_password() {
  local password
  if ! password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}' 2>/dev/null)"; then
    if kubectl -n "$NAMESPACE" get secret redis-credentials >/dev/null 2>&1; then
      echo "==> Secret redis-credentials: no infra/redis-auth here, keeping the existing one"
      return
    fi
    echo "No Secret infra/redis-auth: start the shared Redis first (scripts/deploy.sh $ENVIRONMENT infra), or create" >&2
    echo "the Secret redis-credentials (key password) in $NAMESPACE for another Redis" >&2
    exit 1
  fi
  echo "==> Secret redis-credentials: the shared Redis' password"
  if [[ -n "$(kubectl -n "$NAMESPACE" create secret generic redis-credentials --from-literal=password="$password" \
      --dry-run=client -o yaml | apply_secret)" ]]; then
    restart_api=true
  fi
}

# k3s runs its own containerd, which does not see Docker's images: copy them in, with their names and tags (the
# Deployments use imagePullPolicy IfNotPresent, so the node runs the copy and never asks a registry).
import_into_k3s() {
  local sudo=sudo
  if [[ "$(id -u)" == 0 ]]; then sudo=""; fi
  echo "==> Copying the images into k3s (docker save | ${sudo:+sudo }k3s ctr images import)"
  docker save "$@" | $sudo k3s ctr images import - >/dev/null
  printf '    %s\n' "$@"
}

require_secret() {
  local name="$1" hint="$2"
  if ! kubectl -n "$NAMESPACE" get secret "$name" >/dev/null 2>&1; then
    echo "No Secret $name in $NAMESPACE: $hint" >&2
    exit 1
  fi
}

wait_for_rollouts() {
  local deployment
  for deployment in api web gateway; do
    kubectl -n "$NAMESPACE" rollout status "deployment/$deployment" --timeout=300s
  done
}

# ─── local: build, migrate, apply ─────────────────────────────────────────────────────────────────────────────────

deploy_local_apps() {
  local backend frontend api_tag web_tag build="$ENV_DIR/generated/build"
  backend="$(cd "${BACKEND_DIR:-$ROOT/../backend}" 2>/dev/null && pwd)" \
    || { echo "No backend checkout at ${BACKEND_DIR:-$ROOT/../backend} (clone it next to this repository, or set BACKEND_DIR)" >&2; exit 1; }
  frontend="$(cd "${FRONTEND_DIR:-$ROOT/../frontend}" 2>/dev/null && pwd)" \
    || { echo "No frontend checkout at ${FRONTEND_DIR:-$ROOT/../frontend} (clone it next to this repository, or set FRONTEND_DIR)" >&2; exit 1; }

  "$HERE/local-secrets.sh"

  if ! docker info >/dev/null 2>&1; then
    echo "Docker is not reachable (docker info): start Docker Desktop, or the Docker Engine on Linux; there, your user" >&2
    echo "must be in the group docker (scripts/setup-ubuntu.sh adds it; log in again or run: newgrp docker)" >&2
    exit 1
  fi

  api_tag="$(image_tag "$backend")"
  web_tag="$(image_tag "$frontend")"
  echo "==> Building product-management/api:$api_tag ($backend)"
  docker build --quiet --tag "product-management/api:$api_tag" "$backend" >/dev/null
  echo "==> Building product-management/web:$web_tag and product-management/gateway:$web_tag ($frontend)"
  # APP_VERSION: the release the web app reports with its browser errors.
  docker build --quiet --tag "product-management/web:$web_tag" --build-arg APP_VERSION="$web_tag" "$frontend" >/dev/null
  docker build --quiet --tag "product-management/gateway:$web_tag" "$frontend/gateway" >/dev/null
  if [[ "$CLUSTER" == k3s ]]; then
    import_into_k3s "product-management/api:$api_tag" "product-management/web:$web_tag" \
      "product-management/gateway:$web_tag"
  fi

  mkdir -p "$build"
  # The tags are quoted: a commit like 1186184 would otherwise be a YAML number, which Kustomize rejects.
  cat > "$build/kustomization.yaml" <<EOF
# Generated by scripts/deploy.sh local: the images it built on this machine. Not committed.
apiVersion: kustomize.config.k8s.io/v1alpha1
kind: Component
images:
  - name: product-management/api
    newTag: "$api_tag"
  - name: product-management/web
    newTag: "$web_tag"
  - name: product-management/gateway
    newTag: "$web_tag"
EOF
  if [[ "${LOGS:-on}" == off ]]; then
    cat >> "$build/kustomization.yaml" <<EOF
# LOGS=off: no log collector, the gateway drops the browser's error reports (answers /collect with 204).
patches:
  - patch: |-
      apiVersion: apps/v1
      kind: Deployment
      metadata: { name: gateway, namespace: $NAMESPACE }
      spec:
        template:
          spec:
            containers:
              - name: gateway
                env:
                  - { name: COLLECTOR_UPSTREAM, value: "" }
EOF
  fi

  kubectl apply -f "$ROOT/apps/backend/namespace.yaml" >/dev/null
  ensure_database
  copy_redis_password
  "$HERE/migrate.sh" local

  echo "==> Applying environments/local"
  kubectl apply -k "$ENV_DIR"
}

# The service's database and its two users, on the first deploy to a cluster. The migrations then create everything
# inside it (tables, indexes, the app user's permissions) as the schema owner; they cannot create the database or the
# users themselves: that needs the server's administrator, and the passwords must never be in the code.
# create-database.sh is idempotent and keeps existing passwords, so this only acts when the Secret is missing.
ensure_database() {
  if ! kubectl -n "$NAMESPACE" get secret database-credentials >/dev/null 2>&1; then
    echo "==> The service's database and users (first deploy to this cluster)"
    "$HERE/create-database.sh" "$NAMESPACE" product_management pm_app pm_migrator
  fi
}

# ─── staging, production: apply what is committed ─────────────────────────────────────────────────────────────────

deploy_remote_apps() {
  local changed=""
  rendered="$(mktemp)"
  trap 'rm -f "$rendered"' EXIT
  kubectl kustomize "$ENV_DIR" > "$rendered"
  if grep -q ':unreleased$' "$rendered"; then
    echo "environments/$ENVIRONMENT names no version yet for some images (newTag: unreleased):" >&2
    grep ':unreleased$' "$rendered" | sort -u >&2
    echo "Choose released versions first: scripts/set-version.sh $ENVIRONMENT all latest (commit it), or for staging" >&2
    echo "the update:staging job, once backend and frontend have released a version (a vX.Y.Z tag)." >&2
    exit 1
  fi

  kubectl apply -f "$ROOT/apps/backend/namespace.yaml"
  ensure_database

  # The registries are private: the nodes pull with this credential, also long after this job (a pod moved to another
  # node, a restart), so it is a deploy token, not the job's own short-lived token.
  if [[ -n "${CI_DEPLOY_USER:-}" && -n "${CI_DEPLOY_PASSWORD:-}" ]]; then
    echo "==> Secret gitlab-registry: the deploy token, to pull the images"
    kubectl -n "$NAMESPACE" create secret docker-registry gitlab-registry \
        --docker-server="${CI_REGISTRY:-registry.gitlab.com}" \
        --docker-username="$CI_DEPLOY_USER" --docker-password="$CI_DEPLOY_PASSWORD" \
        --dry-run=client -o yaml | apply_secret >/dev/null
  fi
  require_secret gitlab-registry \
    "set CI_DEPLOY_USER and CI_DEPLOY_PASSWORD (a group deploy token named gitlab-deploy-token, scope read_registry)"

  # The API's own keys. Changing the CI/CD variable and re-running the deploy job is how a key is rotated.
  if [[ -n "${API_SIGNING_KEY:-}" && -n "${API_CONSUMERS:-}" ]]; then
    grep -q -- '-----BEGIN PRIVATE KEY-----' "$API_SIGNING_KEY" \
      || { echo "API_SIGNING_KEY is not a PKCS#8 PEM private key (docker run --rm <api image> generate-signing-key)" >&2; exit 1; }
    jq -e '.Authentication.Consumers | length > 0' "$API_CONSUMERS" >/dev/null \
      || { echo 'API_CONSUMERS must be JSON of the form {"Authentication":{"Consumers":[...]}}' >&2; exit 1; }
    echo "==> Secret api-secrets: from API_SIGNING_KEY and API_CONSUMERS"
    changed="$(kubectl -n "$NAMESPACE" create secret generic api-secrets \
        --from-file=signing-key="$API_SIGNING_KEY" --from-file=consumers.json="$API_CONSUMERS" \
        --dry-run=client -o yaml | apply_secret)"
  fi
  require_secret api-secrets "set API_SIGNING_KEY and API_CONSUMERS (File-type CI/CD variables of the environment)"
  if [[ -n "$changed" ]]; then
    restart_api=true
  fi

  copy_redis_password

  # The schema first, with exactly the API image about to be rolled out; a failed migration stops here.
  "$HERE/migrate.sh" "$ENVIRONMENT"

  echo "==> Applying environments/$ENVIRONMENT"
  kubectl apply -f "$rendered"
}

# ─── main ─────────────────────────────────────────────────────────────────────────────────────────────────────────

if [[ "$TARGET" == infra || "$TARGET" == all ]]; then
  "$HERE/infra.sh" up
fi

if [[ "$TARGET" == apps || "$TARGET" == all ]]; then
  restart_api=false
  if [[ "$ENVIRONMENT" == local ]]; then
    deploy_local_apps
  else
    deploy_remote_apps
  fi
  wait_for_rollouts
  # Pods read their Secrets when they start, and a changed Secret does not change the Deployment.
  if $restart_api; then
    echo "==> A Secret of the API changed: restarting it so it reads it"
    kubectl -n "$NAMESPACE" rollout restart deployment/api
    kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s
  fi
  echo
  kubectl -n "$NAMESPACE" get pods -l 'app.kubernetes.io/name in (api, web, gateway)'
  if [[ "$ENVIRONMENT" == local ]]; then
    # Docker Desktop publishes the LoadBalancer on localhost; k3s (ServiceLB) on the node's addresses.
    address="$(kubectl -n "$NAMESPACE" get service gateway \
      -o jsonpath='{.status.loadBalancer.ingress[0].ip}{.status.loadBalancer.ingress[0].hostname}')"
    echo "Application: http://${address:-localhost}:8080"
    echo "Swagger:     kubectl -n $NAMESPACE port-forward deployment/api 5080:8080   →   http://localhost:5080/swagger"
  fi
fi
