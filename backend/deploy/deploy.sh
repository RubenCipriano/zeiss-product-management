#!/usr/bin/env bash
# Builds the API image, pushes it to the cluster registry and applies backend/deploy/kubernetes.yaml with that
# image version.
#
#   KUBECONFIG=.kube/microk8s backend/deploy/deploy.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BACKEND="$(cd "$HERE/.." && pwd)"
NAMESPACE=product-management
# shellcheck source=../../scripts/lib/images.sh
source "$BACKEND/../scripts/lib/images.sh"

TAG="$(image_tag "$BACKEND")"
IMAGE="product-management/api:$TAG"

echo "==> Building $IMAGE"
docker build --quiet --tag "$IMAGE" "$BACKEND" >/dev/null

echo "==> Pushing $IMAGE to the cluster registry"
push_image "$IMAGE" "$IMAGE"

echo "==> Applying backend manifests"
# Jobs are immutable: the migration Job is recreated for every deployment.
kubectl -n "$NAMESPACE" delete job db-migrate --ignore-not-found >/dev/null 2>&1 || true
sed "s#localhost:32000/product-management/api:latest#localhost:32000/$IMAGE#g" "$HERE/kubernetes.yaml" \
  | kubectl apply -f -

echo "==> Waiting for PostgreSQL, Redis and Keycloak"
kubectl -n "$NAMESPACE" rollout status statefulset/postgres --timeout=300s
kubectl -n "$NAMESPACE" rollout status deployment/redis --timeout=180s
kubectl -n "$NAMESPACE" rollout status deployment/keycloak --timeout=600s

echo "==> Waiting for database migrations"
kubectl -n "$NAMESPACE" wait --for=condition=complete job/db-migrate --timeout=300s

echo "==> Waiting for the API rollout"
kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s

kubectl -n "$NAMESPACE" get pods -l app.kubernetes.io/part-of=product-management
