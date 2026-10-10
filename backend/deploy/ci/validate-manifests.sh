#!/usr/bin/env bash
# Renders the API's Kustomize base and every overlay and checks the result. Run by CI (GitLab and GitHub Actions) in
# the alpine/k8s image, which has kubectl (with Kustomize), kubeconform and yq; works anywhere those three are installed.
#
#   deploy/ci/validate-manifests.sh
#
# For each of deploy/k8s/base and deploy/k8s/overlays/*:
#   1. it renders (kubectl kustomize);
#   2. every object is valid against the Kubernetes API schemas (kubeconform -strict: unknown fields are errors);
#   3. every overlay sets both connection strings (the base leaves them to the environments);
#   4. staging and production contain no Secret: the pipeline creates it from CI/CD variables
#      (deploy/ci/create-secrets.sh), the repository never holds its values.
#
# The local overlay reads its secrets from generated/ (deploy/local-secrets.sh), which a fresh clone or a CI runner
# does not have: the manifests are rendered from a copy, with placeholders for missing files (their content does not
# change the objects' shape). The repository itself is never written to.
set -euo pipefail

KUBERNETES_VERSION="${KUBERNETES_VERSION:-1.34.0}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
rendered="$work/rendered.yaml"
failed=0

K8S="$work/k8s"
cp -R "$(cd "$(dirname "$0")/../k8s" && pwd)" "$K8S"
rm -rf "$K8S/.build"
generated="$K8S/overlays/local/generated"
mkdir -p "$generated"
[[ -f "$generated/signing-key.pem" ]] || echo placeholder > "$generated/signing-key.pem"
[[ -f "$generated/consumers.json" ]] || echo '{"Authentication":{"Consumers":[]}}' > "$generated/consumers.json"
[[ -f "$generated/passwords.env" ]] || printf 'database-password=placeholder\nredis-password=placeholder\n' > "$generated/passwords.env"

cd "$K8S"
for target in base overlays/*; do
  echo "==> $target"
  if ! kubectl kustomize "$target" > "$rendered"; then
    echo "    does not render" >&2
    failed=1
    continue
  fi

  kubeconform -strict -summary -kubernetes-version "$KUBERNETES_VERSION" - < "$rendered" || failed=1

  if [[ "$target" == overlays/* ]]; then
    for key in ConnectionStrings__ProductManagement ConnectionStrings__Redis; do
      value="$(yq ea "select(.kind == \"Deployment\") | .spec.template.spec.containers[].env[] | select(.name == \"$key\") | .value" "$rendered")"
      if [[ -z "$value" ]]; then
        echo "    $key is not set (deploy/k8s/$target/api.yaml)" >&2
        failed=1
      fi
    done
  fi

  if [[ "$target" != overlays/local ]]; then
    secrets="$(yq ea '[select(.kind == "Secret") | .metadata.name] | .[]' "$rendered")"
    if [[ -n "$secrets" ]]; then
      echo "    Secrets in $target (the pipeline creates them; never commit their values): $secrets" >&2
      failed=1
    fi
  fi
done

if [[ "$failed" != 0 ]]; then
  echo "Manifest validation FAILED" >&2
  exit 1
fi
echo "All manifests render and are valid."
