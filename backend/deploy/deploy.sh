#!/usr/bin/env bash
# Deploys the API to the current kubectl context, from deploy/k8s/overlays/$ENVIRONMENT.
#
#   deploy/deploy.sh                                   # local: secrets, image, migrations, rollout
#   ENVIRONMENT=staging API_IMAGE=... MIGRATIONS=only deploy/deploy.sh   # the pipeline's migrate job (deploy/ci)
#   ENVIRONMENT=staging API_IMAGE=... MIGRATIONS=skip deploy/deploy.sh   # the pipeline's deploy job (deploy/ci)
#
# It:
#   1. locally, creates the API's own secrets if they are missing (deploy/local-secrets.sh);
#   2. locally, builds the API image from this folder (Docker Desktop's Kubernetes uses it straight from the local
#      Docker engine; any other cluster needs API_IMAGE, an image from a registry);
#   3. checks the database's Secret (database-credentials) and copies the shared Redis' password into the namespace
#      (redis-credentials);
#   4. applies the database migrations (and, on an empty database, the sample data) with that image, in a one-off pod
#      inside the cluster;
#   5. applies the overlay with the image pinned, and waits for the rollout.
# The shared PostgreSQL and Redis must be running and the database created (the infra repository: `infra.sh up`, then
# `create-database.sh product-management product_management pm_app pm_migrator`).
#
# Variables:
#   ENVIRONMENT                    local | staging | production (default: local)
#   API_IMAGE                      the image to deploy (registry/name:tag or name@sha256:digest); no local build
#   MIGRATIONS                     run (default) | skip: already applied by the pipeline | only: migrate, no rollout
#   MIGRATION_REDIS                Redis, so the migration can invalidate cached lists
#                                  (default: redis.infra.svc.cluster.local:6379)
#   SEED_SAMPLE_DATA               sample catalogue on an empty database (default: true)
set -euo pipefail

DEPLOY="$(cd "$(dirname "$0")" && pwd)"
BACKEND="$(cd "$DEPLOY/.." && pwd)"
ENVIRONMENT="${ENVIRONMENT:-local}"
NAMESPACE=product-management

if [[ ! -d "$DEPLOY/k8s/overlays/$ENVIRONMENT" ]]; then
  echo "Unknown environment: $ENVIRONMENT (see deploy/k8s/overlays)" >&2
  exit 1
fi

# Unique, immutable tag per build: the commit, plus a timestamp when the working tree has local changes.
image_tag() {
  local tag
  tag="$(git -C "$BACKEND" rev-parse --short HEAD 2>/dev/null || echo dev)"
  if [[ -n "$(git -C "$BACKEND" status --porcelain 2>/dev/null)" ]]; then
    tag="$tag-dirty-$(date +%Y%m%d%H%M%S)"
  fi
  echo "$tag"
}

# One Kustomize `images` entry: name → reference (registry/name:tag or name@sha256:digest).
pin_image() {
  local name="$1" ref="$2"
  if [[ "$ref" == *@* ]]; then
    printf '  - name: %s\n    newName: %s\n    digest: %s\n' "$name" "${ref%@*}" "${ref#*@}"
  else
    printf '  - name: %s\n    newName: %s\n    newTag: %s\n' "$name" "${ref%:*}" "${ref##*:}"
  fi
}

# The Secrets the API reads from the shared infrastructure, in its own namespace (a pod reads only its namespace's):
# database-credentials, made by the infra repository's create-database.sh, and redis-credentials, copied here from
# infra/redis-auth on every deploy (so a new Redis password reaches the API on the next deploy).
shared_secrets() {
  kubectl apply -f "$DEPLOY/k8s/base/namespace.yaml" >/dev/null
  if ! kubectl -n "$NAMESPACE" get secret database-credentials >/dev/null 2>&1; then
    echo "No Secret database-credentials in $NAMESPACE: create the database first (the infra repository):" >&2
    echo "  ./create-database.sh $NAMESPACE product_management pm_app pm_migrator" >&2
    exit 1
  fi
  local redis_password
  if ! redis_password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}' 2>/dev/null)"; then
    # Another Redis than the shared one: its password in a redis-credentials Secret made by hand (key `password`).
    if kubectl -n "$NAMESPACE" get secret redis-credentials >/dev/null 2>&1; then
      echo "==> Secret redis-credentials: no infra/redis-auth here, keeping the existing one"
      return
    fi
    echo "No Secret infra/redis-auth: start the shared Redis first (the infra repository: infra.sh up redis)," >&2
    echo "or, for another Redis, create the Secret redis-credentials (key password) in $NAMESPACE" >&2
    exit 1
  fi
  echo "==> Secret redis-credentials: the shared Redis' password"
  local output
  output="$(kubectl -n "$NAMESPACE" create secret generic redis-credentials --from-literal=password="$redis_password" \
      --dry-run=client -o yaml \
    | kubectl label --local -f - -o yaml app.kubernetes.io/part-of=product-management \
    | kubectl apply -f -)"
  if [[ "$output" == *" configured"* ]]; then
    redis_password_changed=true
  fi
}

# Applies the database migrations with the API image itself (`dotnet ProductManagement.Api.dll migrate`), as the
# schema owner, before the rollout: the code that migrates is exactly the code being deployed. The database is only
# reachable inside the cluster, so it runs there as a one-off pod: `kubectl run --rm --attach` streams its output,
# returns its exit code and leaves nothing behind (no Job manifest to keep, no port-forward). The migrator's user,
# password and the database's address come from database-credentials, Redis' password from redis-credentials; the
# kubelet fills in $(DB_HOST) and the others from the variables above them. No password is on a command line.
run_migrations() {
  local name pull_secrets=""
  name="migrate-$(echo "${API_IMAGE##*[:@]}" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9\n' '-' | cut -c1-40)"
  name="${name%-}"
  if [[ "$ENVIRONMENT" != local ]]; then
    pull_secrets='"imagePullSecrets": [{"name": "gitlab-registry-backend"}],'
  fi
  echo "==> Applying the database migrations with $API_IMAGE (pod $name)"
  kubectl -n "$NAMESPACE" delete pod "$name" --ignore-not-found >/dev/null
  kubectl -n "$NAMESPACE" run "$name" --rm --attach --restart=Never --quiet --image="$API_IMAGE" \
    --labels=app.kubernetes.io/name=migrate,app.kubernetes.io/part-of=product-management \
    --overrides="$(cat <<EOF
{
  "apiVersion": "v1",
  "spec": {
    $pull_secrets
    "automountServiceAccountToken": false,
    "securityContext": {"runAsNonRoot": true, "runAsUser": 1654, "seccompProfile": {"type": "RuntimeDefault"}},
    "containers": [{
      "name": "$name",
      "image": "$API_IMAGE",
      "args": ["migrate"],
      "env": [
        {"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"},
        {"name": "Database__SeedSampleData", "value": "${SEED_SAMPLE_DATA:-true}"},
        {"name": "DB_HOST", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "host"}}},
        {"name": "DB_PORT", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "port"}}},
        {"name": "DB_NAME", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "database"}}},
        {"name": "DB_USER", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "migrator-user"}}},
        {"name": "PGPASSWORD", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "migrator-password"}}},
        {"name": "ConnectionStrings__ProductManagement",
         "value": "Host=\$(DB_HOST);Port=\$(DB_PORT);Database=\$(DB_NAME);Username=\$(DB_USER);GSS Encryption Mode=Disable"},
        {"name": "ConnectionStrings__Redis", "value": "${MIGRATION_REDIS:-redis.infra.svc.cluster.local:6379}"},
        {"name": "Redis__Password", "valueFrom": {"secretKeyRef": {"name": "redis-credentials", "key": "password"}}}
      ],
      "resources": {"requests": {"cpu": "100m", "memory": "192Mi"}, "limits": {"cpu": "1", "memory": "512Mi"}},
      "securityContext": {"allowPrivilegeEscalation": false, "capabilities": {"drop": ["ALL"]}}
    }]
  }
}
EOF
)"
}

CONTEXT="$(kubectl config current-context)"
echo "Deploying the API ($ENVIRONMENT) to Kubernetes context: $CONTEXT"

# Without API_IMAGE the image is built here, and only Docker Desktop's Kubernetes can run it as is: it shares the
# Docker engine that built it. Any other cluster (staging, production) pulls from a registry, so it needs API_IMAGE.
if [[ -z "${API_IMAGE:-}" && "$CONTEXT" != docker-desktop ]]; then
  echo "No API_IMAGE given and the context is $CONTEXT, not docker-desktop: a locally built image would not reach" >&2
  echo "this cluster. Push the image to a registry and pass it, e.g. API_IMAGE=registry.example.com/api:1.2.3" >&2
  exit 1
fi

if [[ "$ENVIRONMENT" == local ]]; then
  "$DEPLOY/local-secrets.sh"
fi

if [[ -z "${API_IMAGE:-}" ]]; then
  image="product-management/api:$(image_tag)"
  echo "==> Building $image"
  docker build --quiet --tag "$image" "$BACKEND" >/dev/null
  API_IMAGE="$image"
fi

redis_password_changed=false
shared_secrets
case "${MIGRATIONS:-run}" in
  skip) echo "==> Migrations: already applied by the pipeline" ;;
  only)
    run_migrations
    # The pipeline's migrate job copied a new Redis password: the running API needs it now, not at the next rollout.
    if $redis_password_changed && kubectl -n "$NAMESPACE" get deployment api >/dev/null 2>&1; then
      kubectl -n "$NAMESPACE" rollout restart deployment/api
    fi
    exit 0
    ;;
  *) run_migrations ;;
esac

# The overlay with this release's image pinned (Kustomize `images`): the manifests are never edited by text.
# Kept in deploy/k8s/.build/ (not committed): the pipeline stores it as the record of what was applied.
build="$DEPLOY/k8s/.build/$ENVIRONMENT"
mkdir -p "$build"
cat > "$build/kustomization.yaml" <<EOF
# Generated by deploy/deploy.sh: the $ENVIRONMENT overlay with this release's image. Not committed.
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources: [../../overlays/$ENVIRONMENT]
images:
$(pin_image product-management/api "$API_IMAGE")
EOF

echo "==> Applying overlays/$ENVIRONMENT"
kubectl kustomize "$build" | kubectl apply -f -
kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s
# The pods read the Redis password when they start, and a changed Secret does not change the Deployment.
if $redis_password_changed; then
  echo "==> Redis' password changed: restarting the API so it reads it"
  kubectl -n "$NAMESPACE" rollout restart deployment/api
  kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s
fi

echo
kubectl -n "$NAMESPACE" get pods -l app.kubernetes.io/name=api
echo "Swagger (local and staging): kubectl -n $NAMESPACE port-forward deployment/api 5080:8080   →   http://localhost:5080/swagger"
