#!/usr/bin/env bash
# Prepares a fresh Ubuntu server (22.04 or 24.04) to run the whole system on a single-node k3s, so that
# scripts/infra.sh up and scripts/deploy.sh local work there as they do on Docker Desktop:
#
#   scripts/setup-ubuntu.sh
#
# Installs only what is missing, at pinned versions (safe to run again):
#   packages   git, curl, jq, openssl, python3 (apt)
#   Docker     Docker Engine $DOCKER_VERSION from Docker's apt repository, held at that version; adds you to the group
#              docker. scripts/deploy.sh local builds the images with it when ../backend and ../frontend are here,
#              and copies them into k3s (published images need no Docker: k3s pulls them itself).
#   kubectl    the upstream kubectl at k3s' Kubernetes version (dl.k8s.io)
#   k3s        $K3S_VERSION (the same as scripts/add-node.sh) as a single-node server; its kubeconfig copied to
#              ~/.kube/config (yours, mode 600; context "default"). k3s brings what the system needs: ServiceLB (the
#              gateway on http://<server>:8080), local-path volumes, metrics-server, NetworkPolicies.
# Then prints the next commands (or use the menu: ./menu.sh). Run it as your own user (it uses sudo), with internet
# access.
set -euo pipefail

DOCKER_VERSION="${DOCKER_VERSION:-29.9.0}"
K3S_VERSION="${K3S_VERSION:-v1.34.1+k3s1}"   # = scripts/add-node.sh

if [[ "$(id -u)" == 0 ]]; then
  echo "Run this as your own user, not root: it uses sudo, and sets up Docker and kubectl for you" >&2
  exit 1
fi
# shellcheck source=/dev/null
. /etc/os-release
if [[ "${ID:-}" != ubuntu ]]; then
  echo "This script is for Ubuntu (this is ${PRETTY_NAME:-unknown})" >&2
  exit 1
fi
case "$VERSION_ID" in
  22.04 | 24.04) ;;
  *) echo "Warning: made for Ubuntu 22.04 and 24.04, this is $PRETTY_NAME" >&2 ;;
esac
user="$(id -un)"
started=$SECONDS

# ─── packages ─────────────────────────────────────────────────────────────────────────────────────────────────────

echo "==> Packages: git, curl, jq, openssl, python3"
# No questions, and no service-restart report from needrestart.
apt_install() {
  sudo DEBIAN_FRONTEND=noninteractive NEEDRESTART_SUSPEND=1 apt-get install -y -qq "$@" >/dev/null
}
sudo apt-get update -qq
apt_install ca-certificates curl git jq openssl python3

# ─── Docker Engine ────────────────────────────────────────────────────────────────────────────────────────────────

# The package version as Docker's repository names it, e.g. 5:29.9.0-1~ubuntu.24.04~noble.
docker_package="5:$DOCKER_VERSION-1~ubuntu.$VERSION_ID~$VERSION_CODENAME"
if [[ "$(dpkg-query -W -f='${Version}' docker-ce 2>/dev/null || true)" == "$docker_package" ]]; then
  echo "==> Docker Engine $DOCKER_VERSION: already installed"
else
  echo "==> Docker Engine $DOCKER_VERSION (Docker's apt repository)"
  sudo install -m 0755 -d /etc/apt/keyrings
  sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  sudo chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc]" \
    "https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" \
    | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -qq
  apt_install --allow-downgrades --allow-change-held-packages \
    "docker-ce=$docker_package" "docker-ce-cli=$docker_package" containerd.io docker-buildx-plugin
  # Held, so `apt upgrade` does not move it; change DOCKER_VERSION and run this script again to upgrade.
  sudo apt-mark hold docker-ce docker-ce-cli >/dev/null
fi
sudo systemctl enable --now docker >/dev/null 2>&1
if ! id -nG "$user" | tr ' ' '\n' | grep -qx docker; then
  echo "    adding $user to the group docker (applies to new sessions: log in again, or run newgrp docker)"
  sudo usermod -aG docker "$user"
fi

# ─── kubectl ──────────────────────────────────────────────────────────────────────────────────────────────────────

# The upstream kubectl at the cluster's version, installed before k3s so that k3s does not link its own: k3s' kubectl
# reads k3s' kubeconfig (root's) and cannot change it, this one uses yours, ~/.kube/config (below).
kubectl_version="${K3S_VERSION%%+*}"
if [[ ! -L /usr/local/bin/kubectl \
  && "$(kubectl version --client -o json 2>/dev/null | jq -r .clientVersion.gitVersion)" == "$kubectl_version" ]]; then
  echo "==> kubectl $kubectl_version: already installed"
else
  echo "==> kubectl $kubectl_version (dl.k8s.io, checksum verified)"
  download="https://dl.k8s.io/release/$kubectl_version/bin/linux/$(dpkg --print-architecture)/kubectl"
  work="$(mktemp -d)"
  curl -fsSLo "$work/kubectl" "$download"
  echo "$(curl -fsSL "$download.sha256")  $work/kubectl" | sha256sum --check --quiet
  sudo rm -f /usr/local/bin/kubectl   # k3s' link to itself, if an earlier install made one
  sudo install -m 0755 "$work/kubectl" /usr/local/bin/kubectl
  rm -rf "$work"
fi

# ─── k3s ──────────────────────────────────────────────────────────────────────────────────────────────────────────

if [[ "$(k3s --version 2>/dev/null | head -1)" == *"$K3S_VERSION"* ]]; then
  echo "==> k3s $K3S_VERSION: already installed"
else
  echo "==> k3s $K3S_VERSION (single-node server, get.k3s.io)"
  # The official installer: the k3s binary and its systemd service (k3s.service).
  curl -sfL https://get.k3s.io | sudo INSTALL_K3S_VERSION="$K3S_VERSION" INSTALL_K3S_EXEC=server sh - 2>&1 \
    | sed 's/^/    /'
fi

echo "==> Waiting for the node and k3s' add-ons (CoreDNS, local-path, metrics-server)"
for _ in $(seq 1 60); do
  if sudo k3s kubectl get deployment -n kube-system metrics-server >/dev/null 2>&1; then
    break
  fi
  sleep 5
done
sudo k3s kubectl wait --for=condition=Ready node --all --timeout=180s | sed 's/^/    /'
for deployment in coredns local-path-provisioner metrics-server; do
  sudo k3s kubectl -n kube-system rollout status "deployment/$deployment" --timeout=300s | sed 's/^/    /'
done

# Your kubeconfig: a copy of k3s' own (/etc/rancher/k3s/k3s.yaml, readable only by root), yours alone. To use the
# cluster from a laptop, copy it there and replace 127.0.0.1 with this server's address.
echo "==> ~/.kube/config (context default)"
mkdir -p "$HOME/.kube"
if [[ -f "$HOME/.kube/config" ]] && ! sudo cmp -s /etc/rancher/k3s/k3s.yaml "$HOME/.kube/config"; then
  backup="$HOME/.kube/config.$(date +%Y%m%d%H%M%S).bak"
  echo "    keeping the previous one as $backup"
  mv "$HOME/.kube/config" "$backup"
fi
sudo install -m 600 -o "$user" -g "$(id -gn)" /etc/rancher/k3s/k3s.yaml "$HOME/.kube/config"
kubectl get nodes -o wide | sed 's/^/    /'

# ─── next ─────────────────────────────────────────────────────────────────────────────────────────────────────────

address="$(hostname -I | awk '{print $1}')"
deploy_dir="$(cd "$(dirname "$0")/.." && pwd)"
echo
echo "Ready in $((SECONDS - started)) s: Docker $DOCKER_VERSION, kubectl $kubectl_version, k3s $K3S_VERSION."
echo "Next (the group docker applies to new sessions: log out and in, or start a shell in it with newgrp):"
echo "  newgrp docker"
echo "  cd $deploy_dir"
echo "  scripts/infra.sh up"
if [[ -d "$deploy_dir/../backend" && -d "$deploy_dir/../frontend" ]]; then
  echo "  scripts/deploy.sh local                  # builds the images from ../backend and ../frontend"
else
  echo "  REGISTRY_USER=<GitHub user> REGISTRY_PASSWORD=<token with read:packages> \\"
  echo "    scripts/deploy.sh local --version latest   # the published images (no ../backend, ../frontend here)"
fi
echo "  scripts/ci/smoke.sh http://localhost:8080"
echo "Or all of it from the menu: ./menu.sh"
echo "Then open http://$address:8080 from another machine (port 8080 must be open: sudo ufw allow 8080/tcp if ufw is on)."
