#!/usr/bin/env bash
# Adds a machine to a k3s cluster as a node with a role, so the shared infrastructure can run on machines of its own:
#
#   scripts/add-node.sh <user@host> <database|cache|apps> [--server https://<k3s-server>:6443] [--server-ssh <user@host>]
#                 [--token-file <file>] [--dry-run]
#
#   database   PostgreSQL runs there (infra/postgres.yaml prefers the node and tolerates its taint); nothing else does
#   cache      Redis runs there (infra/redis.yaml); nothing else does
#   apps       an ordinary node for the services (no taint)
#
# Over ssh it installs the k3s agent (pinned: K3S_VERSION) on <user@host> and joins it to the server, with the label
# infra/role=<role> and, for database and cache, the taint infra/role=<role>:NoSchedule from the first moment, so no
# other pod lands there. Then it waits for the node to be Ready and (re)applies the label and taint with kubectl.
#
#   --server       the k3s server's API (default: the server of the current kubectl context)
#   --server-ssh   where to read the join token (/var/lib/rancher/k3s/server/node-token) over ssh
#                  (default: the user of <user@host> at the --server host)
#   --token-file   a local file with the join token instead
#   --dry-run      print the commands, change nothing
#
# Needs ssh access with sudo on the machines and kubectl with this cluster as the current context. It cannot be tried
# on Docker Desktop: its Kubernetes is one fixed node that no machine can join (use --dry-run to see the steps).
set -euo pipefail

K3S_VERSION="${K3S_VERSION:-v1.34.1+k3s1}"   # = the cluster's k3s version (README.md, Nodes)
LABEL_KEY=infra/role

usage() {
  echo "usage: $0 <user@host> <database|cache|apps> [--server URL] [--server-ssh user@host] [--token-file FILE] [--dry-run]" >&2
  exit 1
}

[[ $# -ge 2 ]] || usage
node_ssh="$1"
role="$2"
shift 2
server=""
server_ssh=""
token_file=""
dry_run=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --server) server="${2:?}"; shift 2 ;;
    --server-ssh) server_ssh="${2:?}"; shift 2 ;;
    --token-file) token_file="${2:?}"; shift 2 ;;
    --dry-run) dry_run=true; shift ;;
    *) usage ;;
  esac
done
case "$role" in
  database | cache | apps) ;;
  *) echo "Unknown role: $role (database, cache or apps)" >&2; exit 1 ;;
esac
[[ "$node_ssh" == *@* ]] || { echo "The node must be given as user@host" >&2; exit 1; }

# Runs a command, or only prints it with --dry-run.
run() {
  if $dry_run; then
    echo "+ $*"
  else
    "$@"
  fi
}

if [[ -z "$server" ]]; then
  server="$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')"
fi
if [[ "$server" == *127.0.0.1* || "$server" == *localhost* ]]; then
  echo "The server is $server, which the new node cannot reach: pass --server https://<k3s-server>:6443" >&2
  exit 1
fi
server_host="${server#*://}"
server_host="${server_host%%:*}"
server_ssh="${server_ssh:-${node_ssh%@*}@$server_host}"

echo "==> Join token"
if [[ -n "$token_file" ]]; then
  token="$(cat "$token_file")"
elif $dry_run; then
  echo "+ ssh $server_ssh sudo cat /var/lib/rancher/k3s/server/node-token"
  token="<token>"
else
  token="$(ssh "$server_ssh" sudo cat /var/lib/rancher/k3s/server/node-token)"
fi

# The agent's configuration file, written over ssh's stdin so the token never appears on a command line.
taint_line=""
if [[ "$role" != apps ]]; then
  taint_line="node-taint: [\"$LABEL_KEY=$role:NoSchedule\"]"
fi
config="server: \"$server\"
token: \"$token\"
node-label: [\"$LABEL_KEY=$role\"]
$taint_line"

echo "==> k3s agent $K3S_VERSION on $node_ssh (role $role)"
if $dry_run; then
  echo "+ ssh $node_ssh 'sudo mkdir -p /etc/rancher/k3s && sudo tee /etc/rancher/k3s/config.yaml' <<EOF"
  echo "${config/$token/<token>}"
  echo "EOF"
else
  ssh "$node_ssh" 'sudo mkdir -p /etc/rancher/k3s && sudo tee /etc/rancher/k3s/config.yaml >/dev/null && sudo chmod 600 /etc/rancher/k3s/config.yaml' <<<"$config"
fi
run ssh "$node_ssh" "curl -sfL https://get.k3s.io | sudo INSTALL_K3S_VERSION='$K3S_VERSION' INSTALL_K3S_EXEC=agent sh -"

if $dry_run; then
  node="<hostname of $node_ssh>"
else
  node="$(ssh "$node_ssh" hostname)"
fi

echo "==> Waiting for node $node"
if ! $dry_run; then
  for _ in $(seq 1 60); do
    kubectl get node "$node" >/dev/null 2>&1 && break
    sleep 5
  done
fi
run kubectl wait --for=condition=Ready "node/$node" --timeout=300s

echo "==> Label and taint"
run kubectl label node "$node" "$LABEL_KEY=$role" --overwrite
if [[ "$role" != apps ]]; then
  run kubectl taint node "$node" "$LABEL_KEY=$role:NoSchedule" --overwrite
fi
run kubectl get node "$node" --show-labels

case "$role" in
  # A volume on a node's disk (local-path, hostpath) stays on the node where it was created, and its pod with it.
  database)
    echo "A new PostgreSQL (scripts/infra.sh up postgres) starts there. An existing one stays with its volume on its old node:"
    echo "move it with a dump and restore (README.md, Backups)."
    ;;
  cache) echo "Redis moves there on its next restart: kubectl -n infra rollout restart deployment/redis" ;;
  apps) echo "New pods of the services can now be scheduled there." ;;
esac
