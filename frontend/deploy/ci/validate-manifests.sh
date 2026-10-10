#!/usr/bin/env bash
# Renders the web app's and the gateway's Kustomize base and every overlay and checks the result. Run by CI (GitLab
# and GitHub Actions) in the alpine/k8s image, which has kubectl (with Kustomize), kubeconform and yq; works anywhere
# those three are installed.
#
#   deploy/ci/validate-manifests.sh
#
# For each of deploy/k8s/base and deploy/k8s/overlays/*:
#   1. it renders (kubectl kustomize);
#   2. every object is valid against the Kubernetes API schemas (kubeconform -strict: unknown fields are errors);
#   3. every overlay tells the gateway where the API is (API_UPSTREAM, JWKS_URL) and sets COLLECTOR_UPSTREAM
#      (which may be "": no log collector);
#   4. no Secret anywhere: the web app and the gateway need none.
set -euo pipefail

KUBERNETES_VERSION="${KUBERNETES_VERSION:-1.34.0}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
rendered="$work/rendered.yaml"
failed=0

cd "$(dirname "$0")/../k8s"
for target in base overlays/*; do
  echo "==> $target"
  if ! kubectl kustomize "$target" > "$rendered"; then
    echo "    does not render" >&2
    failed=1
    continue
  fi

  kubeconform -strict -summary -kubernetes-version "$KUBERNETES_VERSION" - < "$rendered" || failed=1

  if [[ "$target" == overlays/* ]]; then
    for key in API_UPSTREAM JWKS_URL COLLECTOR_UPSTREAM; do
      found="$(yq ea "select(.kind == \"Deployment\" and .metadata.name == \"gateway\") | .spec.template.spec.containers[].env[] | select(.name == \"$key\") | .name" "$rendered")"
      if [[ -z "$found" ]]; then
        echo "    $key is not set (deploy/k8s/$target/gateway.yaml)" >&2
        failed=1
      fi
    done
  fi

  secrets="$(yq ea '[select(.kind == "Secret") | .metadata.name] | .[]' "$rendered")"
  if [[ -n "$secrets" ]]; then
    echo "    Secrets in $target (the web app and the gateway need none): $secrets" >&2
    failed=1
  fi
done

if [[ "$failed" != 0 ]]; then
  echo "Manifest validation FAILED" >&2
  exit 1
fi
echo "All manifests render and are valid."
