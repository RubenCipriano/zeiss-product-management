#!/usr/bin/env bash
# What the pipeline's deploy job runs for staging and production (.gitlab-ci.yml): the API's Secret from CI/CD
# variables, then deploy/deploy.sh with the image the pipeline built, scanned and already migrated the database for.
#
#   ENVIRONMENT=staging API_IMAGE=... KUBE_CONTEXT=... deploy/ci/deploy-environment.sh
#
#   ENVIRONMENT    staging | production (deploy/k8s/overlays/<environment>)
#   API_IMAGE      the release's image (registry/name:tag or name@sha256:digest)
#   KUBE_CONTEXT   the kubectl context of the environment's cluster; with the GitLab agent for Kubernetes:
#                  <path of the project holding the agent's configuration>:<agent name>
#   plus the variables of deploy/ci/create-secrets.sh
#
# The migrations are not run here: the pipeline's migrate job ran them, with the same API image, before this job.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
: "${ENVIRONMENT:?ENVIRONMENT is not set (staging or production)}"
: "${API_IMAGE:?API_IMAGE is not set}"
if [[ "$ENVIRONMENT" == local ]]; then
  echo "deploy-environment.sh is for the pipeline's environments; locally run deploy/deploy.sh" >&2
  exit 1
fi

if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT"
fi

echo "==> Secret api-secrets: from the $ENVIRONMENT CI/CD variables"
changed="$("$HERE/create-secrets.sh")"

MIGRATIONS=skip "$HERE/../deploy.sh"

# Secrets are read when a pod starts, and a changed Secret does not change the Deployment, so the rollout above does
# not pick it up by itself. After a rotation, restart the API, one pod at a time like any rollout (when the image
# changed as well, this is a second, harmless rolling restart).
if [[ -n "$changed" ]]; then
  echo "==> The Secret changed: restarting the API so it reads it"
  kubectl -n product-management rollout restart deployment/api
  kubectl -n product-management rollout status deployment/api --timeout=300s
fi
