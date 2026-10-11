#!/usr/bin/env bash
# Checks every environment and the shared infrastructure without touching a cluster. The pipeline's validate job runs
# it on every merge request and commit (scripts/ci/: run by the pipeline; by hand before you push, if you like); it
# works anywhere kubectl (with Kustomize), kubeconform, yq and jq are installed, e.g. in the pipeline's image:
#
#   scripts/ci/validate.sh
#   docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 scripts/ci/validate.sh
#
# environments/local, environments/staging, environments/production:
#   1. it renders (kubectl kustomize);
#   2. every object is valid against the Kubernetes API schemas (kubeconform -strict: unknown fields are errors);
#   3. every object is labelled app.kubernetes.io/part-of=product-management;
#   4. the API has both connection strings and applies its migrations at startup as the migrator (user and password
#      from the Secret database-credentials), with a sample-data setting (never on in production); the gateway knows
#      where the API, its keys and the log collector are;
#   5. staging and production: no Secret (scripts/deploy.sh creates them from CI/CD variables, the repository never
#      holds their values), every Deployment pulls with the Secret gitlab-registry, and every image is a registry
#      image with a tag; local: the only Secret is api-secrets, generated from files that are never committed.
#   local is rendered twice, once per image source of scripts/deploy.sh local: images built on this machine, and
#   published images (registry names, a version, the pull Secret gitlab-registry: the checks of staging in 5).
# infra/:
#   6. postgres.yaml, redis.yaml, logs.yaml and the dashboards' ConfigMap (as scripts/infra.sh renders it) are valid;
#      every dashboard is valid JSON;
#   7. every object is labelled app.kubernetes.io/part-of=infra and, but the Namespace, app.kubernetes.io/component
#      postgres, redis or logs (scripts/infra.sh down selects by it); no Secret (infra.sh makes them in the cluster).
# argocd/ (prepared for Argo CD, not used by the pipeline yet):
#   8. the Applications and the ImageUpdater are valid against the custom resource definitions of the Argo CD and
#      Argo CD Image Updater versions the README installs (downloaded from their release tags, -strict), and each
#      Application points at an environment folder that exists.
#
# The local environment reads files from environments/local/generated/ (made by scripts/deploy.sh local), which a
# fresh clone or a CI runner does not have: it is rendered from a copy, with placeholders for missing files (their
# content does not change the objects' shape), and the image component in the two shapes deploy.sh writes. The
# repository itself is never written to.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
KUBERNETES_VERSION="${KUBERNETES_VERSION:-1.34.0}"
# The versions of README.md, "Moving to Argo CD".
ARGOCD_VERSION="${ARGOCD_VERSION:-v3.5.4}"
IMAGE_UPDATER_VERSION="${IMAGE_UPDATER_VERSION:-v1.3.1}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
rendered="$work/rendered.yaml"
failed=0

fail() {
  echo "    FAIL: $*" >&2
  failed=1
}

# The values of a yq expression over every rendered object, one per line.
query() {
  yq ea "[$1] | .[]" "$rendered"
}

# A yq expression over one environment variable of the API's container (e.g. .value).
api_env() {
  query "select(.kind == \"Deployment\" and .metadata.name == \"api\") | .spec.template.spec.containers[].env[] | select(.name == \"$1\") | $2"
}

cp -R "$ROOT/apps" "$ROOT/environments" "$work/"
generated="$work/environments/local/generated"
mkdir -p "$generated/build"
[[ -f "$generated/signing-key.pem" ]] || echo placeholder > "$generated/signing-key.pem"
[[ -f "$generated/consumers.json" ]] || echo '{"Authentication":{"Consumers":[]}}' > "$generated/consumers.json"

# The image component in the two shapes scripts/deploy.sh local writes (Kustomize rejects a component with nothing in
# it as "empty"). Built here: local names and tags. Published: registry names, a version and the pull Secret.
built_images() {
  cat <<'EOF'
apiVersion: kustomize.config.k8s.io/v1alpha1
kind: Component
images:
  - name: product-management/api
    newTag: validate
  - name: product-management/web
    newTag: validate
  - name: product-management/gateway
    newTag: validate
EOF
}
published_images() {
  cat <<'EOF'
apiVersion: kustomize.config.k8s.io/v1alpha1
kind: Component
images:
  - name: product-management/api
    newName: registry.gitlab.com/zeiss/backend/api
    newTag: "1.0.0"
  - name: product-management/web
    newName: registry.gitlab.com/zeiss/frontend/web
    newTag: "1.0.0"
  - name: product-management/gateway
    newName: registry.gitlab.com/zeiss/frontend/gateway
    newTag: "1.0.0"
patches:
  - target: { kind: Deployment }
    patch: |-
      - op: add
        path: /spec/template/spec/imagePullSecrets
        value: [{ name: gitlab-registry }]
EOF
}
[[ -f "$generated/build/kustomization.yaml" ]] || built_images > "$generated/build/kustomization.yaml"

# local-published: environments/local with published images (deploy.sh local --version).
for environment in local local-published staging production; do
  folder="${environment%-published}"
  if [[ "$environment" == local-published ]]; then
    echo "==> environments/local, published images"
    published_images > "$generated/build/kustomization.yaml"
  else
    echo "==> environments/$environment"
  fi
  if ! kubectl kustomize "$work/environments/$folder" > "$rendered"; then
    fail "does not render"
    continue
  fi

  kubeconform -strict -summary -kubernetes-version "$KUBERNETES_VERSION" - < "$rendered" || failed=1

  unlabelled="$(query 'select(.metadata.labels["app.kubernetes.io/part-of"] != "product-management") | .kind + "/" + .metadata.name')"
  [[ -z "$unlabelled" ]] || fail "not labelled app.kubernetes.io/part-of=product-management: $unlabelled"

  for key in ConnectionStrings__ProductManagement ConnectionStrings__Redis; do
    value="$(query "select(.kind == \"Deployment\" and .metadata.name == \"api\") | .spec.template.spec.containers[].env[] | select(.name == \"$key\") | .value")"
    [[ -n "$value" ]] || fail "$key is not set (environments/$folder/api.yaml)"
  done
  [[ "$(api_env Database__ApplyMigrationsOnStartup .value)" == true ]] \
    || fail "Database__ApplyMigrationsOnStartup is not \"true\" (apps/backend/deployment.yaml)"
  for key in Database__MigratorUsername:migrator-user Database__MigratorPassword:migrator-password; do
    [[ "$(api_env "${key%%:*}" '.valueFrom.secretKeyRef.name + "/" + .valueFrom.secretKeyRef.key')" == "database-credentials/${key#*:}" ]] \
      || fail "${key%%:*} does not come from the Secret database-credentials, key ${key#*:} (apps/backend/deployment.yaml)"
  done
  seed="$(api_env Database__SeedSampleData .value)"
  [[ "$seed" == true || "$seed" == false ]] \
    || fail "Database__SeedSampleData is not \"true\" or \"false\" (environments/$folder/api.yaml)"
  [[ "$environment" != production || "$seed" == false ]] || fail "production seeds sample data"
  for key in API_UPSTREAM JWKS_URL COLLECTOR_UPSTREAM; do
    found="$(query "select(.kind == \"Deployment\" and .metadata.name == \"gateway\") | .spec.template.spec.containers[].env[] | select(.name == \"$key\") | .name")"
    [[ -n "$found" ]] || fail "$key is not set (environments/$folder/gateway.yaml)"
  done

  secrets="$(query 'select(.kind == "Secret") | .metadata.name')"
  if [[ "$folder" == local ]]; then
    others="$(grep -v '^api-secrets-' <<<"$secrets" || true)"
    [[ -z "$others" ]] || fail "Secrets other than the generated api-secrets: $others"
  else
    [[ -z "$secrets" ]] || fail "Secrets (scripts/deploy.sh creates them; never commit their values): $secrets"
  fi
  # Images built on this machine have local names and need no pull Secret.
  [[ "$environment" != local ]] || continue

  for deployment in api web gateway; do
    pull="$(query "select(.kind == \"Deployment\" and .metadata.name == \"$deployment\") | .spec.template.spec.imagePullSecrets[].name")"
    [[ "$pull" == gitlab-registry ]] \
      || fail "$deployment does not pull with imagePullSecrets gitlab-registry (environments/$folder/*.yaml)"
  done
  while read -r image; do
    [[ "$image" == */*/*:* ]] \
      || fail "image $image is not a registry image with a tag (images: in environments/$folder/kustomization.yaml)"
  done < <(query 'select(.kind == "Deployment") | .spec.template.spec.containers[].image')
done

echo "==> infra"
for file in "$ROOT"/infra/dashboards/*.json; do
  jq empty "$file" || fail "$(basename "$file") is not valid JSON"
done
{
  for file in postgres redis logs; do
    cat "$ROOT/infra/$file.yaml"
    echo "---"
  done
  kubectl create configmap grafana-dashboards --namespace infra --from-file="$ROOT/infra/dashboards/" \
    --dry-run=client -o yaml
} > "$rendered"
kubeconform -strict -summary -kubernetes-version "$KUBERNETES_VERSION" - < "$rendered" || failed=1
# (The dashboards' ConfigMap is labelled by infra.sh when it applies it, so it is left out of the label checks.)
unlabelled="$(query 'select(.metadata.name != "grafana-dashboards" and .metadata.labels["app.kubernetes.io/part-of"] != "infra") | .kind + "/" + .metadata.name')"
[[ -z "$unlabelled" ]] || fail "not labelled app.kubernetes.io/part-of=infra: $unlabelled"
unlabelled="$(query 'select(.kind != "Namespace" and .metadata.name != "grafana-dashboards" and ((.metadata.labels["app.kubernetes.io/component"] // "") | test("^(postgres|redis|logs)$") | not)) | .kind + "/" + .metadata.name')"
[[ -z "$unlabelled" ]] || fail "no app.kubernetes.io/component postgres, redis or logs (infra.sh down needs it): $unlabelled"
secrets="$(query 'select(.kind == "Secret") | .metadata.name')"
[[ -z "$secrets" ]] || fail "Secrets in infra/ (infra.sh creates them in the cluster): $secrets"

echo "==> argocd/applications (Argo CD $ARGOCD_VERSION, Image Updater $IMAGE_UPDATER_VERSION)"
# A JSON schema per custom resource, from its CRD; -strict needs "additionalProperties: false" where the CRD lists
# the properties (what kubeconform's own schemas have).
crd_schema() {
  local url="$1" file="$2"
  curl -fsSL "$url" \
    | yq -o json '.spec.versions[] | select(.name == "v1alpha1") | .schema.openAPIV3Schema' \
    | jq 'walk(if type == "object" and has("properties") and (has("additionalProperties") | not)
              then .additionalProperties = false else . end)' > "$file"
}
mkdir -p "$work/schemas"
if crd_schema "https://raw.githubusercontent.com/argoproj/argo-cd/$ARGOCD_VERSION/manifests/crds/application-crd.yaml" \
      "$work/schemas/application_v1alpha1.json" \
  && crd_schema "https://raw.githubusercontent.com/argoproj-labs/argocd-image-updater/$IMAGE_UPDATER_VERSION/config/crd/bases/argocd-image-updater.argoproj.io_imageupdaters.yaml" \
      "$work/schemas/imageupdater_v1alpha1.json"; then
  kubectl kustomize "$ROOT/argocd/applications" > "$rendered"
  kubeconform -strict -summary -kubernetes-version "$KUBERNETES_VERSION" -schema-location default \
    -schema-location "$work/schemas/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json" - < "$rendered" || failed=1
  while read -r path; do
    [[ -f "$ROOT/$path/kustomization.yaml" ]] || fail "an Application points at $path, which is not an environment"
  done < <(query 'select(.kind == "Application") | .spec.source.path')
else
  fail "could not download the Argo CD / Image Updater CRDs (network?)"
fi

if [[ "$failed" != 0 ]]; then
  echo "Validation FAILED" >&2
  exit 1
fi
echo "Every environment, the infrastructure and the Argo CD objects render and are valid."
