#!/usr/bin/env bash
# Applies the database migrations of the API image an environment names, before that environment is applied. The one
# migration path of the system: scripts/deploy.sh runs it for every environment (local, staging, production), right
# before `kubectl apply`, and a person can run it alone.
#
#   scripts/migrate.sh <local|staging|production>
#
# Which image: the one the environment would run, read from the rendered environments/<environment> (for staging and
# production the `images:` newTag in its kustomization.yaml, e.g. registry.gitlab.com/zeiss/backend/api:1.4.0; for
# local the tag scripts/deploy.sh just built). So the schema always comes from exactly the code about to be deployed.
#
# How: the database is reachable only inside the cluster, so the image runs there as a one-off pod with the argument
# `migrate` (`dotnet ProductManagement.Api.dll migrate`). `kubectl run --rm --attach` streams its output, waits for it
# to finish, returns its exit code (a failed migration fails this script, the deploy and the pipeline before anything
# is applied) and deletes the pod. It connects as the schema owner, with the host, database, user and password of the
# Secret database-credentials (scripts/create-database.sh); no password is on a command line or in a CI/CD variable.
# It also invalidates the cached lists in Redis (the environment's ConnectionStrings__Redis, the password from the
# Secret redis-credentials).
#
# The migration must work with the version still running (expand/contract): the old pods keep serving while it runs,
# and next to the new pods during the rollout. A rollback never undoes a migration; a bad one is fixed forward.
#
# Variables:
#   KUBE_CONTEXT       the kubectl context to use (the GitLab agent: <project path>:<agent name>); default: current
#   API_IMAGE          migrate with this image instead of the environment's (e.g. to test an image first)
#   SEED_SAMPLE_DATA   the 24 sample products on an empty database (default: true for local and staging, false for
#                      production)
# Needs: kubectl, and the Secrets database-credentials and redis-credentials in product-management (scripts/deploy.sh
# makes redis-credentials; it pulls with the Secret gitlab-registry when that exists).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NAMESPACE=product-management

ENVIRONMENT="${1:-}"
case "$ENVIRONMENT" in
  local | staging) seed_default=true ;;
  production) seed_default=false ;;
  *)
    echo "usage: $0 <local|staging|production>" >&2
    exit 1
    ;;
esac
SEED="${SEED_SAMPLE_DATA:-$seed_default}"

# The cluster: KUBE_CONTEXT, or the current context. The laptop's cluster is never staging or production.
if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT" >/dev/null
fi
if ! context="$(kubectl config current-context 2>/dev/null)"; then
  echo "No Kubernetes context: set KUBE_CONTEXT (GitLab agent) or KUBECONFIG (a kubeconfig file)" >&2
  exit 1
fi
if [[ "$ENVIRONMENT" != local && "$context" == docker-desktop ]]; then
  echo "$ENVIRONMENT runs on its own cluster, not docker-desktop: set KUBE_CONTEXT or KUBECONFIG" >&2
  exit 1
fi

# The rendered environment: the API image (the container `api` of the Deployment `api`) and the Redis address it uses.
if ! rendered="$(kubectl kustomize "$ROOT/environments/$ENVIRONMENT")"; then
  echo "environments/$ENVIRONMENT does not render (for local: run scripts/deploy.sh local, it writes generated/)" >&2
  exit 1
fi
api_container() {
  kubectl create --dry-run=client -f - -o go-template="{{ if and (eq .kind \"Deployment\") (eq .metadata.name \"api\") }}{{ range .spec.template.spec.containers }}{{ if eq .name \"api\" }}$1{{ end }}{{ end }}{{ end }}" <<<"$rendered"
}
image="${API_IMAGE:-$(api_container '{{ .image }}')}"
redis="$(api_container '{{ range .env }}{{ if eq .name "ConnectionStrings__Redis" }}{{ .value }}{{ end }}{{ end }}')"
redis="${redis:-redis.infra.svc.cluster.local:6379}"
if [[ -z "$image" ]]; then
  echo "No API image in environments/$ENVIRONMENT (Deployment api, container api)" >&2
  exit 1
fi
if [[ "$image" == *:unreleased ]]; then
  echo "environments/$ENVIRONMENT still names no API version ($image): scripts/set-version.sh $ENVIRONMENT api <version>" >&2
  exit 1
fi

for secret in database-credentials redis-credentials; do
  if ! kubectl -n "$NAMESPACE" get secret "$secret" >/dev/null 2>&1; then
    echo "No Secret $secret in $NAMESPACE: scripts/deploy.sh $ENVIRONMENT creates it (database-credentials once, with" >&2
    echo "scripts/create-database.sh $NAMESPACE product_management pm_app pm_migrator)" >&2
    exit 1
  fi
done

# A registry image needs the pull Secret (staging, production); a local image is already on the node.
pull_secrets=""
if kubectl -n "$NAMESPACE" get secret gitlab-registry >/dev/null 2>&1; then
  pull_secrets='"imagePullSecrets": [{"name": "gitlab-registry"}],'
fi

# One pod name per image version (lower case, at most 40 characters of the tag).
name="migrate-$(echo "${image##*[:@]}" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9\n' '-' | cut -c1-40)"
name="${name%-}"
echo "==> Applying the database migrations of $image to $ENVIRONMENT (pod $name, sample data: $SEED)"
kubectl -n "$NAMESPACE" delete pod "$name" --ignore-not-found >/dev/null
kubectl -n "$NAMESPACE" run "$name" --rm --attach --restart=Never --quiet --image="$image" \
  --pod-running-timeout=5m \
  --labels=app.kubernetes.io/name=migrate,app.kubernetes.io/part-of=product-management \
  --overrides="$(cat <<EOF
{
  "apiVersion": "v1",
  "spec": {
    $pull_secrets
    "automountServiceAccountToken": false,
    "securityContext": {"runAsNonRoot": true, "runAsUser": 1654, "seccompProfile": {"type": "RuntimeDefault"}},
    "containers": [{
      "name": "$name",
      "image": "$image",
      "args": ["migrate"],
      "env": [
        {"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"},
        {"name": "Database__SeedSampleData", "value": "$SEED"},
        {"name": "DB_HOST", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "host"}}},
        {"name": "DB_PORT", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "port"}}},
        {"name": "DB_NAME", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "database"}}},
        {"name": "DB_USER", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "migrator-user"}}},
        {"name": "PGPASSWORD", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "migrator-password"}}},
        {"name": "ConnectionStrings__ProductManagement",
         "value": "Host=\$(DB_HOST);Port=\$(DB_PORT);Database=\$(DB_NAME);Username=\$(DB_USER);GSS Encryption Mode=Disable"},
        {"name": "ConnectionStrings__Redis", "value": "$redis"},
        {"name": "Redis__Password", "valueFrom": {"secretKeyRef": {"name": "redis-credentials", "key": "password"}}}
      ],
      "resources": {"requests": {"cpu": "100m", "memory": "192Mi"}, "limits": {"cpu": "1", "memory": "512Mi"}},
      "securityContext": {"allowPrivilegeEscalation": false, "capabilities": {"drop": ["ALL"]}}
    }]
  }
}
EOF
)"
echo "==> Migrations applied ($image)"
