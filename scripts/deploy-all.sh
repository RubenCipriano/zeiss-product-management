#!/usr/bin/env bash
# Deploys backend then frontend to the current kubectl context (each part can also be deployed on its own).
#
#   scripts/deploy-all.sh                                # Docker Desktop's Kubernetes (context docker-desktop)
#   KUBECONFIG=.kube/microk8s scripts/deploy-all.sh      # the MicroK8s VM created by scripts/cluster-up.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
echo "Deploying to Kubernetes context: $(kubectl config current-context)"

"$ROOT/backend/deploy/deploy.sh"
"$ROOT/frontend/deploy/deploy.sh"
