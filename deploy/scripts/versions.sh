#!/usr/bin/env bash
# Lists the versions of the application's images that are published in the container registry, newest first: the
# versions an environment can be set to (scripts/set-version.sh). Read-only.
#
#   scripts/versions.sh <api|web|gateway|all> [--stable] [--builds] [--registry <host>]
#
#   scripts/versions.sh api               # 1.4.0, 1.4.0-rc.1, 1.3.2, ... (SemVer tags, newest first)
#   scripts/versions.sh all               # the same for api, web and gateway, under a heading each
#   scripts/versions.sh web --stable      # without pre-releases (-rc.N)
#   scripts/versions.sh api --builds      # also the commit builds (<short sha> tags) of every pipeline on main
#
# A version is a tag X.Y.Z or X.Y.Z-rc.N (SemVer): the backend and frontend pipelines publish it when someone tags a
# commit vX.Y.Z. Every other tag (the commit builds, the build cache) is not a version. Sorting follows SemVer:
# 1.10.0 > 1.9.0, and 1.4.0 > 1.4.0-rc.2 > 1.4.0-rc.1.
#
# Where: the repositories named in environments/staging/kustomization.yaml (images[].newName), e.g.
# registry.gitlab.com/zeiss/backend/api. --registry (or REGISTRY) replaces their registry host, e.g. a mirror or a
# test registry: --registry localhost:5050 (plain http for localhost and 127.0.0.1; or give the scheme:
# --registry http://host:port).
#
# How: the registry's HTTP API (the Docker Registry v2 / OCI distribution API that every registry has), with curl:
# GET /v2/<repository>/tags/list, following its pages. When the registry asks for a token (GitLab's does), the script
# gets one from the registry's token service for "pull" only, with these credentials, the first that is set:
#   REGISTRY_USER, REGISTRY_PASSWORD     any registry login, e.g. a personal or deploy token with read_registry
#   CI_DEPLOY_USER, CI_DEPLOY_PASSWORD   in GitLab CI: the group deploy token named gitlab-deploy-token
#                                        (read_registry), which GitLab passes to every job by itself
#   your `docker login`                  ~/.docker/config.json, including a credential helper (macOS keychain)
# No credential is printed or put on a command line (curl reads it from stdin).
# Needs: bash, curl, jq, yq (all in the pipeline's alpine/k8s image).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KUSTOMIZATION="$ROOT/environments/staging/kustomization.yaml"
SEMVER='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.(0|[1-9][0-9]*))?$'
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

usage() {
  echo "usage: $0 <api|web|gateway|all> [--stable] [--builds] [--registry <host>]" >&2
  exit 1
}

target="${1:-}"
case "$target" in api | web | gateway | all) ;; *) usage ;; esac
shift
stable=false
builds=false
registry="${REGISTRY:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --stable) stable=true ;;
    --builds) builds=true ;;
    --registry) [[ $# -ge 2 ]] || usage; registry="$2"; shift ;;
    *) usage ;;
  esac
  shift
done

# The repository of an image (api, web, gateway), from staging's kustomization.yaml, with --registry applied.
# Prints "<scheme>://<host> <path>", e.g. "https://registry.gitlab.com zeiss/backend/api".
repository() {
  local name host path scheme
  name="$(IMAGE="product-management/$1" yq '.images[] | select(.name == strenv(IMAGE)) | .newName' "$KUSTOMIZATION")"
  [[ -n "$name" && "$name" == */* ]] \
    || { echo "No newName for product-management/$1 in environments/staging/kustomization.yaml" >&2; exit 1; }
  host="${name%%/*}"
  path="${name#*/}"
  if [[ -n "$registry" ]]; then
    host="$registry"
  fi
  case "$host" in
    http://* | https://*) scheme="${host%%://*}"; host="${host#*://}" ;;
    localhost | localhost:* | 127.0.0.1 | 127.0.0.1:*) scheme=http ;;
    *) scheme=https ;;
  esac
  echo "$scheme://$host $path"
}

# "user:password" for a registry host, or nothing (anonymous): REGISTRY_*, then CI_DEPLOY_*, then docker login.
credentials() {
  local host="$1" config="${DOCKER_CONFIG:-$HOME/.docker}/config.json" helper auth
  if [[ -n "${REGISTRY_USER:-}" && -n "${REGISTRY_PASSWORD:-}" ]]; then
    echo "$REGISTRY_USER:$REGISTRY_PASSWORD"
  elif [[ -n "${CI_DEPLOY_USER:-}" && -n "${CI_DEPLOY_PASSWORD:-}" ]]; then
    echo "$CI_DEPLOY_USER:$CI_DEPLOY_PASSWORD"
  elif [[ -f "$config" ]]; then
    helper="$(HOST="$host" jq -r '.credHelpers[env.HOST] // .credsStore // empty' "$config")"
    if [[ -n "$helper" ]] && command -v "docker-credential-$helper" >/dev/null; then
      "docker-credential-$helper" get <<<"$host" 2>/dev/null | jq -r 'select(.Secret != null) | .Username + ":" + .Secret' || true
    else
      auth="$(HOST="$host" jq -r '.auths[env.HOST].auth // empty' "$config")"
      [[ -z "$auth" ]] || base64 -d <<<"$auth" 2>/dev/null || true
    fi
  fi
}

# Every tag of one repository, one per line (all pages).
list_tags() {
  local base="$1" path="$2" host="${1#*://}" url status challenge realm service token="" login next
  local headers="$work/headers" body="$work/body"
  url="$base/v2/$path/tags/list?n=1000"
  while [[ -n "$url" ]]; do
    # The token goes in through curl's config on stdin (-K -), like the password below: never on a command line.
    status="$( { [[ -z "$token" ]] || printf 'header = "Authorization: Bearer %s"\n' "$token"; } \
      | curl -sS -K - -o "$body" -D "$headers" -w '%{http_code}' "$url")"
    if [[ "$status" == 401 && -z "$token" ]]; then
      # The registry wants a token: ask its token service (realm) for pull rights on this repository.
      challenge="$(tr -d '\r' < "$headers" | grep -i '^www-authenticate: bearer ' | head -1 || true)"
      realm="$(sed -n 's/.*realm="\([^"]*\)".*/\1/p' <<<"$challenge")"
      service="$(sed -n 's/.*service="\([^"]*\)".*/\1/p' <<<"$challenge")"
      [[ -n "$realm" ]] || { echo "$host refused the request (401) and offers no token service" >&2; exit 1; }
      login="$(credentials "$host")"
      if [[ -n "$login" ]]; then
        login="${login//\\/\\\\}"
        login="${login//\"/\\\"}"
        token="$(printf 'user = "%s"\n' "$login" | curl -sS -K - -G "$realm" \
          --data-urlencode "service=$service" --data-urlencode "scope=repository:$path:pull" | jq -r '.token // .access_token // empty')"
      else
        token="$(curl -sS -G "$realm" --data-urlencode "service=$service" \
          --data-urlencode "scope=repository:$path:pull" | jq -r '.token // .access_token // empty')"
      fi
      [[ -n "$token" ]] || { echo "No registry token for $host/$path: check the credentials (read_registry)" >&2; exit 1; }
      continue
    fi
    case "$status" in
      200) ;;
      401 | 403) echo "Not allowed to read $host/$path ($status): check the credentials (read_registry)" >&2; exit 1 ;;
      404) return 0 ;; # nothing published yet
      *) echo "$host answered $status for $path" >&2; exit 1 ;;
    esac
    jq -r '.tags // [] | .[]' "$body"
    # The next page, if any: Link: </v2/<path>/tags/list?last=...&n=...>; rel="next"
    next="$(tr -d '\r' < "$headers" | grep -i '^link: ' | sed -n 's/^[Ll]ink: *<\([^>]*\)>.*rel="next".*/\1/p' || true)"
    if [[ -z "$next" ]]; then
      url=""
    elif [[ "$next" == http* ]]; then
      url="$next"
    else
      url="$base$next"
    fi
  done
}

# SemVer tags of stdin, newest first (a release sorts above its pre-releases); --stable drops the pre-releases.
sort_versions() {
  grep -E "$SEMVER" \
    | awk -F'[.-]' '{ rc = ($4 == "") ? 999999999 : $5; print $1, $2, $3, rc, $0 }' \
    | sort -k1,1nr -k2,2nr -k3,3nr -k4,4nr \
    | awk '{ print $5 }' \
    | { if $stable; then grep -v -- '-rc\.' || true; else cat; fi; }
}

show() {
  local image="$1" repo base path tags
  repo="$(repository "$image")"
  base="${repo% *}"
  path="${repo#* }"
  tags="$(list_tags "$base" "$path")"
  if [[ "$target" == all ]]; then
    echo "== $image (${base#*://}/$path)"
  fi
  sort_versions <<<"$tags" || true
  if $builds; then
    echo "-- commit builds (tags <short sha>; the registry keeps no order, see the pipelines for dates):"
    grep -E '^[0-9a-f]{7,40}$' <<<"$tags" | sort || true
  fi
}

if [[ "$target" == all ]]; then
  for image in api web gateway; do
    show "$image"
  done
else
  show "$target"
fi
