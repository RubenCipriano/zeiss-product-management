#!/usr/bin/env bash
# Installs the cluster add-ons the deployments rely on, into the current kubectl context.
#
#   scripts/cluster-addons.sh
#
# metrics-server: reports pod CPU and memory. The API's HorizontalPodAutoscaler needs it to decide how many replicas
# to run, and `kubectl top pods` uses it. MicroK8s ships it as an add-on (scripts/cluster-up.sh enables it); on
# Docker Desktop it is installed from the official release manifest, pinned to a version.
set -euo pipefail
METRICS_SERVER_VERSION="${METRICS_SERVER_VERSION:-v0.8.0}"
context="$(kubectl config current-context)"

if kubectl get apiservice v1beta1.metrics.k8s.io >/dev/null 2>&1; then
  echo "metrics-server is already installed in $context"
else
  echo "==> Installing metrics-server $METRICS_SERVER_VERSION in $context"
  kubectl apply -f "https://github.com/kubernetes-sigs/metrics-server/releases/download/$METRICS_SERVER_VERSION/components.yaml"
  if [[ "$context" == "docker-desktop" ]]; then
    # Docker Desktop's kubelet serves a self-signed certificate that does not name the node's IP. Local clusters
    # only: on a real cluster the kubelet certificates are signed by the cluster CA and this flag is not needed.
    kubectl -n kube-system patch deployment metrics-server --type=json \
      -p '[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
  fi
fi

kubectl -n kube-system rollout status deployment/metrics-server --timeout=180s
echo "==> Waiting for the first metrics"
for _ in $(seq 1 30); do
  kubectl top nodes >/dev/null 2>&1 && { kubectl top nodes; exit 0; }
  sleep 5
done
echo "metrics-server is running but has not reported metrics yet; try 'kubectl top nodes' in a minute" >&2
