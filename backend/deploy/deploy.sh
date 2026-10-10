#!/usr/bin/env bash
# Deploys the API to the current kubectl context, from deploy/k8s/overlays/$ENVIRONMENT.
#
#   deploy/deploy.sh                                   # local: secrets, image, migrations, rollout
#   ENVIRONMENT=staging API_IMAGE=... MIGRATIONS=skip deploy/deploy.sh   # what the pipeline runs (deploy/ci)
#
# Locally (ENVIRONMENT=local, the default) it:
#   1. creates the local secrets if they are missing (deploy/local-secrets.sh);
#   2. builds the API image from this folder (Docker Desktop's Kubernetes uses it straight from the local Docker
#      engine; any other cluster needs API_IMAGE, an image from a registry);
#   3. applies the database migrations (and, on an empty database, the sample data) with that image;
#   4. applies the overlay with the image pinned, and waits for the rollout.
# The database and Redis must be running (the infra repository: `infra.sh up database redis`).
#
# Variables:
#   ENVIRONMENT                    local | staging | production (default: local)
#   API_IMAGE                      the image to deploy (registry/name:tag or name@sha256:digest); no local build
#   MIGRATIONS=skip                the migrations were already applied (the pipeline's migrate job)
#   MIGRATION_CONNECTION_STRING    the migrator's connection string, without password (default: the local database)
#   PM_MIGRATOR_PASSWORD           the migrator's password (default: the local development one)
#   MIGRATION_REDIS                Redis, so the migration can invalidate cached lists (default: the local one)
#   REDIS_PASSWORD                 Redis' password (default: the local development one)
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

# Applies the database migrations with the API image itself, from outside the cluster and as the schema owner
# (pm_migrator): the code that migrates is exactly the code being deployed, and the cluster needs no migration Job
# nor the right to change the schema. The passwords go in PGPASSWORD and Redis__Password, never in a connection string.
run_migrations() {
  echo "==> Applying the database migrations with $API_IMAGE"
  docker run --rm --add-host=host.docker.internal:host-gateway \
    -e ASPNETCORE_ENVIRONMENT=Production \
    -e Database__SeedSampleData="${SEED_SAMPLE_DATA:-true}" \
    -e ConnectionStrings__ProductManagement="${MIGRATION_CONNECTION_STRING:-Host=host.docker.internal;Port=5432;Database=product_management;Username=pm_migrator;GSS Encryption Mode=Disable}" \
    -e PGPASSWORD="${PM_MIGRATOR_PASSWORD:-local-migrator-password}" \
    -e ConnectionStrings__Redis="${MIGRATION_REDIS:-host.docker.internal:6379}" \
    -e Redis__Password="${REDIS_PASSWORD:-local-redis-password}" \
    "$API_IMAGE" migrate
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

if [[ "${MIGRATIONS:-run}" == skip ]]; then
  echo "==> Migrations: already applied by the pipeline"
else
  run_migrations
fi

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

echo
kubectl -n "$NAMESPACE" get pods -l app.kubernetes.io/name=api
echo "Swagger (local and staging): kubectl -n $NAMESPACE port-forward deployment/api 5080:8080   →   http://localhost:5080/swagger"
