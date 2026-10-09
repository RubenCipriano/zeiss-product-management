#!/usr/bin/env bash
# Creates a local single-node MicroK8s cluster in a Multipass VM and writes its kubeconfig to .kube/microk8s.
#
#   scripts/cluster-up.sh            # then: export KUBECONFIG="$PWD/.kube/microk8s"
#
# On Linux you can install MicroK8s directly (`sudo snap install microk8s --classic`) and skip the VM; the
# Kubernetes manifests are the same.
set -euo pipefail

VM_NAME="${VM_NAME:-microk8s-vm}"
VM_CPUS="${VM_CPUS:-4}"
VM_MEMORY="${VM_MEMORY:-6G}"
VM_DISK="${VM_DISK:-25G}"
MICROK8S_CHANNEL="${MICROK8S_CHANNEL:-1.34/stable}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if ! multipass info "$VM_NAME" >/dev/null 2>&1; then
  echo "==> Creating VM $VM_NAME ($VM_CPUS CPUs, $VM_MEMORY RAM, $VM_DISK disk)"
  multipass launch 24.04 --name "$VM_NAME" --cpus "$VM_CPUS" --memory "$VM_MEMORY" --disk "$VM_DISK"
fi

# stdin from /dev/null: multipass exec can otherwise hang waiting on it when run from a script.
run() { multipass exec "$VM_NAME" -- "$@" < /dev/null; }

if ! run snap list microk8s >/dev/null 2>&1; then
  echo "==> Installing MicroK8s ($MICROK8S_CHANNEL)"
  run sudo snap install microk8s --classic --channel="$MICROK8S_CHANNEL"
  run sudo usermod -a -G microk8s ubuntu
fi

run sudo microk8s status --wait-ready --timeout 300 >/dev/null

# dns: service discovery; hostpath-storage: PersistentVolumes for PostgreSQL; registry: image registry at
# localhost:32000 that the deploy scripts push to; metrics-server: `kubectl top` and the optional autoscaler.
echo "==> Enabling add-ons"
run sudo microk8s enable dns hostpath-storage metrics-server
run sudo microk8s enable registry

mkdir -p "$ROOT/.kube"
run sudo microk8s config > "$ROOT/.kube/microk8s"
chmod 600 "$ROOT/.kube/microk8s"

echo
echo "Cluster ready. Use it from this machine with:"
echo "  export KUBECONFIG=\"$ROOT/.kube/microk8s\""
