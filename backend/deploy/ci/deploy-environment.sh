#!/usr/bin/env bash
# What the pipeline's deploy job runs for staging and production (.gitlab-ci.yml): selects the environment's cluster,
# stores the registry credential the pods pull the image with, creates the API's Secret from CI/CD variables, then
# runs deploy/deploy.sh with the image the pipeline built, scanned and already migrated the database for.
#
#   ENVIRONMENT=staging API_IMAGE=... KUBE_CONTEXT=... deploy/ci/deploy-environment.sh
#
#   ENVIRONMENT          staging | production (deploy/k8s/overlays/<environment>)
#   API_IMAGE            the release's image (registry/name:tag or name@sha256:digest)
#   KUBE_CONTEXT         the cluster, through the GitLab agent for Kubernetes:
#                        <path of the project holding the agent's configuration>:<agent name>
#   KUBECONFIG           or a kubeconfig file whose current context is the cluster (File-type CI/CD variable; e.g. a
#                        self-hosted runner next to a k3s server). KUBE_CONTEXT wins: GitLab also sets KUBECONFIG
#                        (the agent's contexts) in every job of a project the agent is shared with
#   CI_DEPLOY_USER       the project's deploy token `gitlab-deploy-token` (scope read_registry), which GitLab passes
#   CI_DEPLOY_PASSWORD   to the jobs by itself → Secret gitlab-registry-backend (the Deployment's imagePullSecrets in
#                        the staging and production overlays)
#   CI_REGISTRY          the registry (GitLab sets it; default: the image's host)
#   plus the variables of deploy/ci/create-secrets.sh
#
# The migrations are not run here: the pipeline's migrate job ran them, with the same API image, before this job.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=product-management
PULL_SECRET=gitlab-registry-backend
: "${ENVIRONMENT:?ENVIRONMENT is not set (staging or production)}"
: "${API_IMAGE:?API_IMAGE is not set}"
if [[ "$ENVIRONMENT" == local ]]; then
  echo "deploy-environment.sh is for the pipeline's environments; locally run deploy/deploy.sh" >&2
  exit 1
fi

# The cluster: the GitLab agent's context, or the kubeconfig's current context.
if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT"
elif [[ -n "${KUBECONFIG:-}" ]]; then
  kubectl config current-context >/dev/null \
    || { echo "The kubeconfig in KUBECONFIG has no current context: set one, or set KUBE_CONTEXT" >&2; exit 1; }
else
  echo "No cluster for $ENVIRONMENT: set KUBE_CONTEXT (GitLab agent) or a KUBECONFIG file variable" >&2
  echo "(CI/CD variables scoped to the $ENVIRONMENT environment; docs/guide/12-from-zero.md)" >&2
  exit 1
fi

# The registry is private, so the nodes need a credential to pull the image, also long after this job (a pod moved
# to another node, a restart). The job's own CI_JOB_TOKEN expires with the job; a deploy token does not.
if [[ -z "${CI_DEPLOY_USER:-}" || -z "${CI_DEPLOY_PASSWORD:-}" ]]; then
  echo "No deploy token: the cluster could not pull $API_IMAGE. Create a deploy token named exactly" >&2
  echo "gitlab-deploy-token with the read_registry scope (Settings → Repository → Deploy tokens, in this project or" >&2
  echo "its group); GitLab then passes it to the jobs as CI_DEPLOY_USER and CI_DEPLOY_PASSWORD." >&2
  exit 1
fi
echo "==> Secret $PULL_SECRET: the deploy token, to pull from the registry"
kubectl apply -f "$HERE/../k8s/base/namespace.yaml"
kubectl -n "$NAMESPACE" create secret docker-registry "$PULL_SECRET" \
    --docker-server="${CI_REGISTRY:-${API_IMAGE%%/*}}" \
    --docker-username="$CI_DEPLOY_USER" \
    --docker-password="$CI_DEPLOY_PASSWORD" \
    --dry-run=client -o yaml \
  | kubectl label --local -f - -o yaml \
      app.kubernetes.io/part-of=product-management app.kubernetes.io/managed-by=ci \
  | kubectl apply -f -

echo "==> Secret api-secrets: from the $ENVIRONMENT CI/CD variables"
changed="$("$HERE/create-secrets.sh")"

MIGRATIONS=skip "$HERE/../deploy.sh"

# Secrets are read when a pod starts, and a changed Secret does not change the Deployment, so the rollout above does
# not pick it up by itself. After a rotation, restart the API, one pod at a time like any rollout (when the image
# changed as well, this is a second, harmless rolling restart).
if [[ -n "$changed" ]]; then
  echo "==> The Secret changed: restarting the API so it reads it"
  kubectl -n "$NAMESPACE" rollout restart deployment/api
  kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=300s
fi
