#!/usr/bin/env bash
# Builds the web and gateway images, makes them available to the current Kubernetes context (Docker Desktop or
# MicroK8s) and applies frontend/deploy/kubernetes.yaml with those image versions.
#
#   frontend/deploy/deploy.sh                                  # current kubectl context (e.g. docker-desktop)
#   KUBECONFIG=.kube/microk8s frontend/deploy/deploy.sh        # the MicroK8s VM
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

echo "==> Publishing images to the $(cluster_kind) cluster"
WEB_REF="$(publish_image "$WEB")"
GATEWAY_REF="$(publish_image "$GATEWAY")"

echo "==> Applying frontend manifests"
sed -e "s#localhost:32000/product-management/web:latest#$WEB_REF#g" \
    -e "s#localhost:32000/product-management/gateway:latest#$GATEWAY_REF#g" \
    "$HERE/kubernetes.yaml" | kubectl apply -f -
kubectl -n "$NAMESPACE" rollout status deployment/web --timeout=300s
kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=300s

echo
if [[ "$(cluster_kind)" == docker-desktop ]]; then
  echo "Open the application: http://localhost:8080   (the gateway is a LoadBalancer Service on localhost)"
else
  echo "Open the application:"
  echo "  kubectl -n $NAMESPACE port-forward service/gateway 8080:8080   ->  http://localhost:8080"
fi
