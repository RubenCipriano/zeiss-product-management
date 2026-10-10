#!/usr/bin/env bash
# Applies the database migrations of one API image to an environment's database, before that image is released.
# Run by the pipeline's migrate:staging and migrate:production jobs (.gitlab-ci.yml); needs only kubectl.
#
#   ENVIRONMENT=staging API_IMAGE=registry.gitlab.com/zeiss/backend/api:<sha> ci/migrate.sh
#
# The database is reachable only inside the cluster, so the image runs there as a one-off pod
# (`dotnet ProductManagement.Api.dll migrate`): `kubectl run --rm --attach` streams its output, returns its exit code
# and leaves nothing behind. It connects as the schema owner, with the user, password, host and database of the
# Secret database-credentials (made once per environment by the deploy repository's create-database.sh). No password
# is on a command line or in a CI/CD variable.
#
# The migration must work with the version still running (expand/contract): the old pods keep serving while it runs,
# and next to the new pods during the rollout. It also invalidates the cached lists in Redis, whose password it copies
# from the shared Redis (infra/redis-auth) into the Secret redis-credentials, as the deploy repository does.
#
# Variables:
#   ENVIRONMENT                          staging | production (for the messages; the cluster is what selects it)
#   API_IMAGE                            the image to migrate with (this pipeline's image)
#   KUBE_CONTEXT                         the cluster, through the GitLab agent: <agent configuration project>:<agent>
#   KUBECONFIG                           or a kubeconfig file whose current context is the cluster
#   CI_DEPLOY_USER, CI_DEPLOY_PASSWORD   a deploy token with read_registry (GitLab passes the group deploy token named
#                                        gitlab-deploy-token by itself) → Secret gitlab-registry, to pull API_IMAGE
#   CI_REGISTRY                          the registry (GitLab sets it; default registry.gitlab.com)
#   SEED_SAMPLE_DATA                     sample catalogue on an empty database (default: false)
set -euo pipefail

NAMESPACE=product-management
PULL_SECRET=gitlab-registry
: "${ENVIRONMENT:?ENVIRONMENT is not set (staging or production)}"
: "${API_IMAGE:?API_IMAGE is not set}"

# The cluster: the GitLab agent's context, or the kubeconfig's current context. KUBE_CONTEXT wins: GitLab also sets
# KUBECONFIG (the agent's contexts) in every job of a project the agent is shared with.
if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT"
elif ! kubectl config current-context >/dev/null 2>&1; then
  echo "No cluster for $ENVIRONMENT: set KUBE_CONTEXT (GitLab agent) or a KUBECONFIG file variable" >&2
  exit 1
fi

if ! kubectl -n "$NAMESPACE" get secret database-credentials >/dev/null 2>&1; then
  echo "No Secret database-credentials in $NAMESPACE ($ENVIRONMENT): create the database first, with the deploy" >&2
  echo "repository's scripts/create-database.sh $NAMESPACE product_management pm_app pm_migrator" >&2
  exit 1
fi

# The registry is private. The deploy repository's pipeline keeps this Secret too; on a first release it may not
# exist yet, so it is created here as well (the same name, the same token).
if [[ -n "${CI_DEPLOY_USER:-}" && -n "${CI_DEPLOY_PASSWORD:-}" ]]; then
  echo "==> Secret $PULL_SECRET: the deploy token, to pull the image"
  kubectl -n "$NAMESPACE" create secret docker-registry "$PULL_SECRET" \
      --docker-server="${CI_REGISTRY:-registry.gitlab.com}" \
      --docker-username="$CI_DEPLOY_USER" --docker-password="$CI_DEPLOY_PASSWORD" \
      --dry-run=client -o yaml \
    | kubectl label --local -f - -o yaml app.kubernetes.io/part-of=product-management app.kubernetes.io/managed-by=ci \
    | kubectl apply -f -
fi
pull_secrets=""
if kubectl -n "$NAMESPACE" get secret "$PULL_SECRET" >/dev/null 2>&1; then
  pull_secrets="\"imagePullSecrets\": [{\"name\": \"$PULL_SECRET\"}],"
fi

# The shared Redis' password, in this namespace (a pod reads only its own namespace's Secrets).
redis_changed=false
if redis_password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}' 2>/dev/null)"; then
  output="$(kubectl -n "$NAMESPACE" create secret generic redis-credentials --from-literal=password="$redis_password" \
      --dry-run=client -o yaml \
    | kubectl label --local -f - -o yaml app.kubernetes.io/part-of=product-management \
    | kubectl apply -f -)"
  [[ "$output" != *" configured"* ]] || redis_changed=true
elif ! kubectl -n "$NAMESPACE" get secret redis-credentials >/dev/null 2>&1; then
  echo "No Secret infra/redis-auth nor $NAMESPACE/redis-credentials: start the shared Redis first" >&2
  exit 1
fi

name="migrate-$(echo "${API_IMAGE##*[:@]}" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9\n' '-' | cut -c1-40)"
name="${name%-}"
echo "==> Applying the database migrations of $API_IMAGE to $ENVIRONMENT (pod $name)"
kubectl -n "$NAMESPACE" delete pod "$name" --ignore-not-found >/dev/null
kubectl -n "$NAMESPACE" run "$name" --rm --attach --restart=Never --quiet --image="$API_IMAGE" \
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
      "image": "$API_IMAGE",
      "args": ["migrate"],
      "env": [
        {"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"},
        {"name": "Database__SeedSampleData", "value": "${SEED_SAMPLE_DATA:-false}"},
        {"name": "DB_HOST", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "host"}}},
        {"name": "DB_PORT", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "port"}}},
        {"name": "DB_NAME", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "database"}}},
        {"name": "DB_USER", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "migrator-user"}}},
        {"name": "PGPASSWORD", "valueFrom": {"secretKeyRef": {"name": "database-credentials", "key": "migrator-password"}}},
        {"name": "ConnectionStrings__ProductManagement",
         "value": "Host=\$(DB_HOST);Port=\$(DB_PORT);Database=\$(DB_NAME);Username=\$(DB_USER);GSS Encryption Mode=Disable"},
        {"name": "ConnectionStrings__Redis", "value": "redis.infra.svc.cluster.local:6379"},
        {"name": "Redis__Password", "valueFrom": {"secretKeyRef": {"name": "redis-credentials", "key": "password"}}}
      ],
      "resources": {"requests": {"cpu": "100m", "memory": "192Mi"}, "limits": {"cpu": "1", "memory": "512Mi"}},
      "securityContext": {"allowPrivilegeEscalation": false, "capabilities": {"drop": ["ALL"]}}
    }]
  }
}
EOF
)"

# A new Redis password: the running API reads it only when it starts.
if $redis_changed && kubectl -n "$NAMESPACE" get deployment api >/dev/null 2>&1; then
  echo "==> Redis' password changed: restarting the running API so it reads it"
  kubectl -n "$NAMESPACE" rollout restart deployment/api
fi
