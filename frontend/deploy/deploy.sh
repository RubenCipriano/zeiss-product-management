#!/usr/bin/env bash
# Builds the web and gateway images, pushes them to the cluster registry and applies
# frontend/deploy/kubernetes.yaml with those image versions.
#
#   KUBECONFIG=.kube/microk8s frontend/deploy/deploy.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
FRONTEND="$(cd "$HERE/.." && pwd)"
NAMESPACE=product-management
# shellcheck source=../../scripts/lib/images.sh
source "$FRONTEND/../scripts/lib/images.sh"

TAG="$(image_tag "$FRONTEND")"
WEB="product-management/web:$TAG"
GATEWAY="product-management/gateway:$TAG"

echo "==> Building $WEB and $GATEWAY"
docker build --quiet --tag "$WEB" "$FRONTEND" >/dev/null
docker build --quiet --tag "$GATEWAY" "$FRONTEND/gateway" >/dev/null

echo "==> Pushing images to the cluster registry"
push_image "$WEB" "$WEB"
push_image "$GATEWAY" "$GATEWAY"

echo "==> Applying frontend manifests"
sed -e "s#localhost:32000/product-management/web:latest#localhost:32000/$WEB#g" \
    -e "s#localhost:32000/product-management/gateway:latest#localhost:32000/$GATEWAY#g" \
    "$HERE/kubernetes.yaml" | kubectl apply -f -
kubectl -n "$NAMESPACE" rollout status deployment/web --timeout=300s
kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=300s

echo
echo "Open the application:"
echo "  kubectl -n $NAMESPACE port-forward service/gateway 8080:80   ->  http://localhost:8080"
