#!/usr/bin/env bash
# Creates or updates the API's one Secret, api-secrets, in the current kubectl context from CI/CD variables. Run by
# the pipeline's deploy jobs for staging and production (deploy/ci/deploy-environment.sh); locally the overlay
# generates the same Secret from deploy/k8s/overlays/local/generated (deploy/local-secrets.sh). Idempotent: it runs on
# every deployment, so changing a variable in GitLab and re-running the deploy job is how a secret is rotated.
#
#   api-secrets
#     database-password   password of the database user pm_app        → PGPASSWORD in the API
#     redis-password      password of Redis                           → Redis__Password
#     signing-key         ES256 private key that signs access tokens  → Authentication__SigningKey
#     consumers.json      registered consumers: client ids, PBKDF2 secret hashes, roles → /app/config/consumers.json
#
# Required (GitLab: CI/CD variables scoped to the environment, protected; docs/guide/12-from-zero.md):
#   PM_APP_PASSWORD     password of pm_app                                   masked
#   REDIS_PASSWORD      password of Redis                                    masked
#   API_SIGNING_KEY     path to the PEM file                                 File-type variable
#   API_CONSUMERS       path to consumers.json                               File-type variable
# Optional:
#   DRY_RUN=server      validate everything against the cluster without changing it
#
# Prints "api-secrets" on stdout when the Secret's content changed, so the caller can restart the pods that read it
# at startup; progress goes to stderr.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=product-management
: "${PM_APP_PASSWORD:?PM_APP_PASSWORD is not set (CI/CD variable, masked, scoped to the environment)}"
: "${REDIS_PASSWORD:?REDIS_PASSWORD is not set (CI/CD variable, masked, scoped to the environment)}"
: "${API_SIGNING_KEY:?API_SIGNING_KEY is not set (File-type CI/CD variable with the PEM private key)}"
: "${API_CONSUMERS:?API_CONSUMERS is not set (File-type CI/CD variable with consumers.json)}"
dry_run=()
[[ -n "${DRY_RUN:-}" ]] && dry_run=(--dry-run="$DRY_RUN")

# Fail before touching the cluster when a value is obviously wrong: the API reads consumers.json as optional
# configuration, so a malformed file would start an API that nobody can sign in to.
grep -q -- '-----BEGIN PRIVATE KEY-----' "$API_SIGNING_KEY" \
  || { echo "API_SIGNING_KEY is not a PKCS#8 PEM private key (dotnet ProductManagement.Api.dll generate-signing-key)" >&2; exit 1; }
if command -v jq >/dev/null; then
  jq -e '.Authentication.Consumers | length > 0' "$API_CONSUMERS" >/dev/null \
    || { echo 'API_CONSUMERS must be JSON of the form {"Authentication":{"Consumers":[...]}}' >&2; exit 1; }
fi

# The namespace may not exist yet on a new cluster.
kubectl apply "${dry_run[@]}" -f "$HERE/../k8s/base/namespace.yaml" >&2

# kubectl create --dry-run renders the Secret; apply creates or updates it and says whether it changed.
output="$(kubectl -n "$NAMESPACE" create secret generic api-secrets \
    --from-literal=database-password="$PM_APP_PASSWORD" \
    --from-literal=redis-password="$REDIS_PASSWORD" \
    --from-file=signing-key="$API_SIGNING_KEY" \
    --from-file=consumers.json="$API_CONSUMERS" \
    --dry-run=client -o yaml \
  | kubectl label --local -f - -o yaml \
      app.kubernetes.io/part-of=product-management app.kubernetes.io/managed-by=ci \
  | kubectl apply "${dry_run[@]}" -f -)"
echo "$output" >&2

# "configured" = the content changed (a first creation needs no restart: the pods are created after it).
if [[ "$output" == *" configured"* ]]; then
  echo api-secrets
fi
