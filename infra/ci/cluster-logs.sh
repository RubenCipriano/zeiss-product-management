#!/usr/bin/env bash
# What the pipeline's cluster-logs jobs run (.gitlab-ci.yml): selects the environment's cluster, then applies the
# cluster's log collector with `./infra.sh up logs` (the job's image has kubectl and no Docker, so only the collector).
#
#   KUBE_CONTEXT=... LOKI_URL=... ci/cluster-logs.sh
#
#   KUBE_CONTEXT   the cluster, through the GitLab agent for Kubernetes:
#                  <path of the project holding the agent's configuration>:<agent name>
#   KUBECONFIG     or a kubeconfig file whose current context is the cluster (File-type CI/CD variable; e.g. a
#                  self-hosted runner next to a k3s server). KUBE_CONTEXT wins: GitLab also sets KUBECONFIG (the
#                  agent's contexts) in every job of a project the agent is shared with
#   LOKI_URL, LOKI_USERNAME, LOKI_PASSWORD   see infra.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT"
elif [[ -n "${KUBECONFIG:-}" ]]; then
  kubectl config current-context >/dev/null \
    || { echo "The kubeconfig in KUBECONFIG has no current context: set one, or set KUBE_CONTEXT" >&2; exit 1; }
else
  echo "No cluster: set KUBE_CONTEXT (GitLab agent) or a KUBECONFIG file variable" >&2
  echo "(CI/CD variables scoped to the environment; docs/guide/12-from-zero.md)" >&2
  exit 1
fi

"$HERE/../infra.sh" up logs
