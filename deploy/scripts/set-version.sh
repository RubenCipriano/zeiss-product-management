#!/usr/bin/env bash
# Chooses which published version of an image an environment runs: sets its newTag in
# environments/<environment>/kustomization.yaml. Upgrading and downgrading (rolling back) are the same command. It only
# edits the file and prints the change: commit it yourself (staging: directly on main or through a merge request;
# production: always a merge request). The pipeline's update:staging job runs it for staging and commits.
#
#   scripts/set-version.sh <staging|production> <api|web|gateway|frontend|all> <version|latest> [--pre] [--registry <host>]
#
#   scripts/set-version.sh staging all latest            # every image to its newest released version
#   scripts/set-version.sh production api 1.4.0          # the API to 1.4.0 (up or down)
#   scripts/set-version.sh production frontend 2.1.0     # web and gateway (one frontend release) to 2.1.0
#   scripts/set-version.sh staging api latest --pre      # the newest version including pre-releases (1.5.0-rc.1)
#
# <version> is X.Y.Z or X.Y.Z-rc.N (a leading v is accepted: v1.4.0). It must exist in the registry for every image it
# is set on (scripts/versions.sh lists them): an unknown version is refused and nothing changes.
# `latest` is the newest released version (pre-releases only with --pre). web and gateway are built from one frontend
# tag, so for `frontend` and `all` their latest is the newest version that both have. `latest` never moves an image
# backwards: if the environment already runs a newer version (e.g. a pre-release set by hand), it stays. An image with
# no released version yet keeps its current tag (with a warning).
#
# Variables: REGISTRY, REGISTRY_USER, REGISTRY_PASSWORD (or CI_DEPLOY_USER, CI_DEPLOY_PASSWORD, or your docker login),
# as for scripts/versions.sh. Needs: bash, curl, jq, yq, diff.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SEMVER='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.(0|[1-9][0-9]*))?$'

usage() {
  echo "usage: $0 <staging|production> <api|web|gateway|frontend|all> <version|latest> [--pre] [--registry <host>]" >&2
  exit 1
}

[[ $# -ge 3 ]] || usage
environment="$1"
target="$2"
requested="$3"
version="${requested#v}"
shift 3
case "$environment" in staging | production) ;; *) usage ;; esac
case "$target" in
  api | web | gateway) images=("$target") ;;
  frontend) images=(web gateway) ;;
  all) images=(api web gateway) ;;
  *) usage ;;
esac
pre=false
registry_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --pre) pre=true ;;
    --registry) [[ $# -ge 2 ]] || usage; registry_args=(--registry "$2"); shift ;;
    *) usage ;;
  esac
  shift
done
if [[ "$version" != latest ]] && ! grep -Eq "$SEMVER" <<<"$version"; then
  echo "\"$requested\" is not a version: X.Y.Z or X.Y.Z-rc.N (e.g. 1.4.0), or latest" >&2
  exit 1
fi

FILE="$ROOT/environments/$environment/kustomization.yaml"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$FILE" "$work/before.yaml"

# The published versions of an image, newest first (cached per run).
versions_of() {
  if [[ ! -f "$work/$1" ]]; then
    "$HERE/versions.sh" "$1" ${registry_args[@]+"${registry_args[@]}"} > "$work/$1"
  fi
  if $pre; then cat "$work/$1"; else grep -v -- '-rc\.' "$work/$1" || true; fi
}

current_of() {
  IMAGE="product-management/$1" yq '.images[] | select(.name == strenv(IMAGE)) | .newTag' "$FILE"
}

# 0 when SemVer version $1 is newer than $2 (both from the registry's order: sort the two and compare).
newer() {
  [[ "$1" != "$2" ]] && [[ "$(printf '%s\n%s\n' "$1" "$2" \
    | awk -F'[.-]' '{ rc = ($4 == "") ? 999999999 : $5; print $1, $2, $3, rc, $0 }' \
    | sort -k1,1nr -k2,2nr -k3,3nr -k4,4nr | head -1 | awk '{ print $5 }')" == "$1" ]]
}

# The version each image gets (chosen[i] for images[i]; empty: unchanged).
chosen=()
for i in "${!images[@]}"; do
  image="${images[$i]}"
  chosen[i]=""
  if [[ -z "$(current_of "$image")" ]]; then
    echo "No image product-management/$image in environments/$environment/kustomization.yaml" >&2
    exit 1
  fi
  if [[ "$version" != latest ]]; then
    versions_of "$image" >/dev/null
    if ! grep -Fxq "$version" "$work/$image"; then
      echo "$image $version is not in the registry: scripts/versions.sh $image lists the published versions" >&2
      exit 1
    fi
    chosen[i]="$version"
    continue
  fi
  if [[ "$image" == web || "$image" == gateway ]] && [[ " ${images[*]} " == *" web "* && " ${images[*]} " == *" gateway "* ]]; then
    # One frontend release: the newest version both images have.
    newest="$(grep -Fxf <(versions_of gateway) <(versions_of web) | head -1 || true)"
  else
    newest="$(versions_of "$image" | head -1)"
  fi
  if [[ -z "$newest" ]]; then
    echo "warning: no released version of $image yet; keeping $(current_of "$image")" >&2
    continue
  fi
  current="$(current_of "$image")"
  if grep -Eq "$SEMVER" <<<"$current" && newer "$current" "$newest"; then
    echo "$image: keeping $current (newer than the latest release, $newest)" >&2
    continue
  fi
  chosen[i]="$newest"
done

for i in "${!images[@]}"; do
  [[ -n "${chosen[i]}" ]] || continue
  IMAGE="product-management/${images[$i]}" TAG="${chosen[i]}" \
    yq -i '(.images[] | select(.name == strenv(IMAGE))).newTag = strenv(TAG)' "$FILE"
done

if diff -q "$work/before.yaml" "$FILE" >/dev/null; then
  echo "environments/$environment already runs these versions: nothing changed"
  exit 0
fi
for image in "${images[@]}"; do
  before="$(IMAGE="product-management/$image" yq '.images[] | select(.name == strenv(IMAGE)) | .newTag' "$work/before.yaml")"
  after="$(current_of "$image")"
  [[ "$before" == "$after" ]] || echo "$environment: $image $before → $after"
done
diff -u -L "a/environments/$environment/kustomization.yaml" -L "b/environments/$environment/kustomization.yaml" \
  "$work/before.yaml" "$FILE" || true
