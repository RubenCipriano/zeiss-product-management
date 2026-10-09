#!/usr/bin/env bash
# Deploys backend then frontend to the local cluster (each part can also be deployed on its own).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export KUBECONFIG="${KUBECONFIG:-$ROOT/.kube/microk8s}"

"$ROOT/backend/deploy/deploy.sh"
"$ROOT/frontend/deploy/deploy.sh"
