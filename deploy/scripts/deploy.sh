#!/usr/bin/env bash
# Deploys one environment of the Product Management system to its Kubernetes cluster.
#
#   scripts/deploy.sh <local|staging|production> [infra|apps|all] [--build | --version <X.Y.Z|latest>]
#
#   infra   the shared infrastructure in the namespace infra (PostgreSQL, Redis, logs): scripts/infra.sh up
#   apps    the API, the web app and the gateway in the namespace product-management: environments/<environment>
#           (the default)
#   all     infra, then apps
#
# local: a cluster for one machine (Docker Desktop's Kubernetes, context docker-desktop, on macOS or Windows; or a
# single-node k3s on Ubuntu, context default, set up by scripts/setup-ubuntu.sh). Its images come from one of two
# places (README.md, "Where the images come from"):
#   built      from the sibling checkouts ../backend and ../frontend (BACKEND_DIR, FRONTEND_DIR): for a developer
#              trying local changes. The default when both folders exist; --build insists on it.
#   published  a released version from the registry (registry.gitlab.com/zeiss/...): for a server or a demo with
#              only this repository. The default when the checkouts are missing; --version X.Y.Z (or latest, the
#              newest X.Y.Z that api, web and gateway all have) insists on it. Needs a registry login (below).
# Steps:
#   1. creates the API's local secrets if they are missing (scripts/lib/local-secrets.sh);
#   2. built: builds the api, web and gateway images with Docker and tags them with the checkouts' commits; on k3s
#      copies them into its containerd (docker save | sudo k3s ctr images import; Docker Desktop's Kubernetes runs the
#      images of its Docker engine as they are).
#      published: checks the version exists for all three images, built for the nodes' platform (scripts/versions.sh),
#      and creates the pull Secret gitlab-registry from the registry login; the cluster pulls the images itself;
#   3. records the images in environments/local/generated/build (not committed): their tags, and for published
#      images also their registry names and the pull Secret, so `kubectl diff -k environments/local` compares
#      against exactly what was applied, and the committed environments/local stays the same for both;
#   4. creates the service's database and users if they do not exist yet (scripts/lib/create-database.sh);
#   5. copies the shared Redis' password into the namespace (Secret redis-credentials);
#   6. applies environments/local and waits for the rollouts: the new API pods apply the database migrations
#      themselves when they start, before they serve (apps/backend/deployment.yaml).
#
# staging, production (this repository's pipeline, or a laptop whose kubectl reaches that cluster): never built here
# and never from ../backend or ../frontend; they run the versions committed in environments/<environment>.
#   1. refuses an environment whose kustomization.yaml still names no version for an image (newTag: unreleased);
#   2. creates the registry credential (Secret gitlab-registry) and the API's own Secret (api-secrets) from the CI/CD
#      variables below, when they are set (otherwise the existing Secrets are kept);
#   3. creates the service's database and users on the first deploy (scripts/lib/create-database.sh);
#   4. copies the shared Redis' password into the namespace (Secret redis-credentials);
#   5. applies environments/<environment> exactly as committed and waits for the rollouts: the new API pods apply
#      the database migrations themselves when they start; if one fails, the rollout stops and the old pods keep
#      serving (the job fails at the wait).
#   Which versions run is chosen in environments/<environment>/kustomization.yaml (scripts/set-version.sh).
#
# Variables:
#   KUBE_CONTEXT                          the kubectl context to use (the GitLab agent: <project path>:<agent name>);
#                                         default: the current context (e.g. of a KUBECONFIG file)
#   BACKEND_DIR, FRONTEND_DIR             local, built: the checkouts to build (default: ../backend and ../frontend)
#   LOCAL_CLUSTER=docker-desktop|k3s      local, built: how the images reach the cluster, when the detection gets it
#                                         wrong (default: docker-desktop for that context, k3s for a k3s node here)
#   LOGS=off                              local: no log collector, the gateway answers /collect with 204
#   REGISTRY_USER, REGISTRY_PASSWORD      local, published: a deploy token with read_registry (or CI_DEPLOY_USER and
#                                         CI_DEPLOY_PASSWORD, or this machine's docker login registry.gitlab.com)
#   CI_DEPLOY_USER, CI_DEPLOY_PASSWORD    staging/production: a deploy token with read_registry on both registries
#                                         (GitLab passes the group deploy token named gitlab-deploy-token by itself)
#   CI_REGISTRY                           the registry of that token (default: registry.gitlab.com)
#   API_SIGNING_KEY, API_CONSUMERS        staging/production: files with the ES256 private key (PEM) and consumers.json
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
NAMESPACE=product-management
SEMVER='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.(0|[1-9][0-9]*))?$'
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

usage() {
  echo "usage: $0 <local|staging|production> [infra|apps|all] [--build | --version <X.Y.Z|latest>]" >&2
  exit 1
}

ENVIRONMENT="${1:-}"
[[ $# -eq 0 ]] || shift
TARGET=apps
SOURCE=""        # local: built or published ("": built when the checkouts exist, else published)
VERSION=latest   # local, published
while [[ $# -gt 0 ]]; do
  case "$1" in
    infra | apps | all) TARGET="$1" ;;
    --build) SOURCE=built ;;
    --version) [[ $# -ge 2 ]] || usage; SOURCE=published; VERSION="${2#v}"; shift ;;
    *) usage ;;
  esac
  shift
done
case "$ENVIRONMENT" in local | staging | production) ;; *) usage ;; esac
if [[ "$ENVIRONMENT" != local && -n "$SOURCE" ]]; then
  echo "--build and --version are for local. $ENVIRONMENT runs the versions committed in" >&2
  echo "environments/$ENVIRONMENT/kustomization.yaml: change them with scripts/set-version.sh and commit." >&2
  exit 1
fi
ENV_DIR="$ROOT/environments/$ENVIRONMENT"
BACKEND="${BACKEND_DIR:-$ROOT/../backend}"
FRONTEND="${FRONTEND_DIR:-$ROOT/../frontend}"
if [[ "$ENVIRONMENT" == local && -z "$SOURCE" ]]; then
  if [[ -d "$BACKEND" && -d "$FRONTEND" ]]; then SOURCE=built; else SOURCE=published; fi
fi
deploy_apps=false
if [[ "$TARGET" == apps || "$TARGET" == all ]]; then deploy_apps=true; fi

# ─── the cluster ──────────────────────────────────────────────────────────────────────────────────────────────────

if [[ -n "${KUBE_CONTEXT:-}" ]]; then
  kubectl config use-context "$KUBE_CONTEXT" >/dev/null
fi
if ! CONTEXT="$(kubectl config current-context 2>/dev/null)"; then
  echo "No Kubernetes context: set KUBE_CONTEXT (GitLab agent) or KUBECONFIG (a kubeconfig file)" >&2
  exit 1
fi
# The laptop's cluster is never staging or production.
if [[ "$ENVIRONMENT" != local && "$CONTEXT" == docker-desktop ]]; then
  echo "$ENVIRONMENT runs on its own cluster, not docker-desktop: set KUBE_CONTEXT or KUBECONFIG" >&2
  exit 1
fi

# What kind of cluster this is, for images built on this machine, which it must run without a registry:
#   docker-desktop   Docker Desktop's Kubernetes runs the images of its own Docker engine (macOS, Windows)
#   k3s              a k3s node on this machine (Linux): the images are copied into k3s' containerd
#   other            anything else: it can run published images only
local_cluster() {
  local versions nodes
  if [[ -n "${LOCAL_CLUSTER:-}" ]]; then
    echo "$LOCAL_CLUSTER"
  elif [[ "$CONTEXT" == docker-desktop ]]; then
    echo docker-desktop
  else
    # k3s reports itself in the kubelet version (v1.34.1+k3s1) and names the node after the machine.
    versions="$(kubectl get nodes -o jsonpath='{.items[*].status.nodeInfo.kubeletVersion}' 2>/dev/null || true)"
    nodes=" $(kubectl get nodes -o jsonpath='{.items[*].metadata.name}' 2>/dev/null || true) "
    if [[ "$versions" == *+k3s* && "$nodes" == *" $(hostname | tr '[:upper:]' '[:lower:]') "* ]] \
      && command -v k3s >/dev/null; then
      echo k3s
    else
      echo other
    fi
  fi
}

if [[ "$ENVIRONMENT" == local ]] && $deploy_apps; then
  CLUSTER="$(local_cluster)"
  if [[ "$SOURCE" == built && "$CLUSTER" != docker-desktop && "$CLUSTER" != k3s ]]; then
    echo "Images built on this machine cannot reach the context $CONTEXT without a registry." >&2
    echo "Use Docker Desktop's Kubernetes (context docker-desktop) or a k3s server on this machine" >&2
    echo "(scripts/setup-ubuntu.sh), set LOCAL_CLUSTER=docker-desktop|k3s if the detection is wrong, or run" >&2
    echo "published images: scripts/deploy.sh local --version <X.Y.Z|latest>." >&2
    exit 1
  fi
  echo "Deploying $ENVIRONMENT ($TARGET) to Kubernetes context: $CONTEXT ($CLUSTER)"
else
  echo "Deploying $ENVIRONMENT ($TARGET) to Kubernetes context: $CONTEXT"
fi

# ─── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

# kubectl apply of a Secret rendered by `kubectl create --dry-run`, labelled; prints "changed" when it existed and
# its content changed (pods read Secrets when they start, so the caller restarts them).
apply_secret() {
  local output
  output="$(kubectl label --local -f - -o yaml \
      app.kubernetes.io/part-of=product-management app.kubernetes.io/managed-by=deploy.sh \
    | kubectl apply -f -)"
  echo "    $output" >&2
  if [[ "$output" == *" configured"* ]]; then
    echo changed
  fi
}

# The image pull Secret gitlab-registry: a docker config made with jq from its environment and read by kubectl from
# stdin, so the token is on no command line.
registry_secret() {
  local server="$1" username="$2" password="$3"
  SERVER="$server" USERNAME="$username" PASSWORD="$password" jq -n '{auths: {(env.SERVER): {
      username: env.USERNAME, password: env.PASSWORD, auth: ((env.USERNAME + ":" + env.PASSWORD) | @base64)}}}' \
    | kubectl -n "$NAMESPACE" create secret generic gitlab-registry --type=kubernetes.io/dockerconfigjson \
        --from-file=.dockerconfigjson=/dev/stdin --dry-run=client -o yaml \
    | apply_secret >/dev/null
}

# The commit of a checkout, plus a timestamp when it has uncommitted changes: a unique, immutable tag per build
# (a folder that is not a git checkout gets dev-<timestamp>).
image_tag() {
  local dir="$1" tag
  tag="$(git -C "$dir" rev-parse --short HEAD 2>/dev/null || echo "dev-$(date +%Y%m%d%H%M%S)")"
  if [[ -n "$(git -C "$dir" status --porcelain -- . 2>/dev/null)" ]]; then
    tag="$tag-dirty-$(date +%Y%m%d%H%M%S)"
  fi
  echo "$tag"
}

# The shared Redis' password, copied from infra/redis-auth into this namespace (a pod reads only its own namespace's
# Secrets). Sets restart_api when it changed, so the API is restarted to read it.
copy_redis_password() {
  local password
  if ! password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}' 2>/dev/null)"; then
    if kubectl -n "$NAMESPACE" get secret redis-credentials >/dev/null 2>&1; then
      echo "==> Secret redis-credentials: no infra/redis-auth here, keeping the existing one"
      return
    fi
    echo "No Secret infra/redis-auth: start the shared Redis first (scripts/deploy.sh $ENVIRONMENT infra), or create" >&2
    echo "the Secret redis-credentials (key password) in $NAMESPACE for another Redis" >&2
    exit 1
  fi
  echo "==> Secret redis-credentials: the shared Redis' password"
  if [[ -n "$(kubectl -n "$NAMESPACE" create secret generic redis-credentials --from-literal=password="$password" \
      --dry-run=client -o yaml | apply_secret)" ]]; then
    restart_api=true
  fi
}

# k3s runs its own containerd, which does not see Docker's images: copy them in, with their names and tags (the
# Deployments use imagePullPolicy IfNotPresent, so the node runs the copy and never asks a registry).
import_into_k3s() {
  local sudo=sudo
  if [[ "$(id -u)" == 0 ]]; then sudo=""; fi
  echo "==> Copying the images into k3s (docker save | ${sudo:+sudo }k3s ctr images import)"
  docker save "$@" | $sudo k3s ctr images import - >/dev/null
  printf '    %s\n' "$@"
}

require_secret() {
  local name="$1" hint="$2"
  if ! kubectl -n "$NAMESPACE" get secret "$name" >/dev/null 2>&1; then
    echo "No Secret $name in $NAMESPACE: $hint" >&2
    exit 1
  fi
}

# The API's first new pod applies the migrations before it becomes ready, which can take up to its startup probe's 10
# minutes (apps/backend/deployment.yaml): its rollout gets 15.
wait_for_rollouts() {
  kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=900s
  kubectl -n "$NAMESPACE" rollout status deployment/web --timeout=300s
  kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=300s
}

# The service's database and its two users, on the first deploy to a cluster. They must exist before the API starts:
# its migrations then create everything inside the database (tables, indexes, the app user's permissions) as the
# schema owner (pm_migrator); they cannot create the database or the users themselves: that needs the server's
# administrator, and the passwords must never be in the code.
# create-database.sh is idempotent and keeps existing passwords, so this only acts when the Secret is missing.
ensure_database() {
  if ! kubectl -n "$NAMESPACE" get secret database-credentials >/dev/null 2>&1; then
    echo "==> The service's database and users (first deploy to this cluster)"
    "$HERE/lib/create-database.sh" "$NAMESPACE" product_management pm_app pm_migrator
  fi
}

# ─── local, published images: the registry login and the version ─────────────────────────────────────────────────

# The registry image of api, web or gateway: its newName in environments/staging/kustomization.yaml (the line after
# its "- name:"), the images staging and production run, e.g. registry.gitlab.com/zeiss/backend/api.
registry_image() {
  awk -v image="product-management/$1" '
    $1 == "-" && $2 == "name:" { found = ($3 == image) }
    found && $1 == "newName:" { gsub(/"/, "", $2); print $2; exit }' "$ROOT/environments/staging/kustomization.yaml"
}

# The registry login, the first that is set (the same order as scripts/versions.sh): REGISTRY_USER and
# REGISTRY_PASSWORD, CI_DEPLOY_USER and CI_DEPLOY_PASSWORD, or this machine's `docker login <host>` (also through a
# credential helper such as the macOS keychain). Exported as REGISTRY_USER and REGISTRY_PASSWORD for versions.sh;
# the cluster gets it as the pull Secret gitlab-registry.
registry_login() {
  local host="$1" config="${DOCKER_CONFIG:-$HOME/.docker}/config.json" helper login=""
  if [[ -n "${REGISTRY_USER:-}" && -n "${REGISTRY_PASSWORD:-}" ]]; then
    export REGISTRY_USER REGISTRY_PASSWORD
    return
  fi
  if [[ -n "${CI_DEPLOY_USER:-}" && -n "${CI_DEPLOY_PASSWORD:-}" ]]; then
    login="$CI_DEPLOY_USER:$CI_DEPLOY_PASSWORD"
  elif [[ -f "$config" ]]; then
    helper="$(HOST="$host" jq -r '.credHelpers[env.HOST] // .credsStore // empty' "$config")"
    if [[ -n "$helper" ]] && command -v "docker-credential-$helper" >/dev/null; then
      login="$("docker-credential-$helper" get <<<"$host" 2>/dev/null \
        | jq -r 'select(.Secret != null) | .Username + ":" + .Secret' 2>/dev/null || true)"
    else
      login="$(HOST="$host" jq -r '.auths[env.HOST].auth // empty' "$config" | base64 -d 2>/dev/null || true)"
    fi
  fi
  if [[ "$login" != *:* ]]; then
    echo "Published images need a login to $host: set REGISTRY_USER and REGISTRY_PASSWORD (a deploy token with" >&2
    echo "the scope read_registry, e.g. a group deploy token named server-pull), or run: docker login $host" >&2
    echo "Or build the images from ../backend and ../frontend: clone them next to this repository (--build)." >&2
    exit 1
  fi
  REGISTRY_USER="${login%%:*}"
  REGISTRY_PASSWORD="${login#*:}"
  export REGISTRY_USER REGISTRY_PASSWORD
}

# The version to run: an explicit one must be published for api, web and gateway; latest is the newest X.Y.Z (no
# pre-release) that all three have (web and gateway are one frontend release, api is versioned on its own).
choose_version() {
  local image missing="" published
  if [[ "$VERSION" != latest ]] && ! grep -Eq "$SEMVER" <<<"$VERSION"; then
    echo "\"$VERSION\" is not a version: X.Y.Z or X.Y.Z-rc.N (e.g. 1.0.0), or latest" >&2
    exit 1
  fi
  for image in api web gateway; do
    # versions.sh explains a registry error itself (no access, no token).
    "$HERE/versions.sh" "$image" > "$work/$image"
  done
  if [[ "$VERSION" == latest ]]; then
    VERSION="$(grep -v -- '-rc\.' "$work/api" | grep -Fx -f "$work/web" | grep -Fx -f "$work/gateway" | head -1 || true)"
    if [[ -z "$VERSION" ]]; then
      echo "No released version X.Y.Z is published for all of api, web and gateway yet (scripts/versions.sh all)." >&2
      echo "Release one (a vX.Y.Z tag in backend and frontend), or build the images from ../backend and ../frontend." >&2
      exit 1
    fi
    return
  fi
  for image in api web gateway; do
    grep -Fxq "$VERSION" "$work/$image" || missing="$missing $image"
  done
  if [[ -n "$missing" ]]; then
    echo "Version $VERSION is not published for:$missing. Published (newest first; scripts/versions.sh all):" >&2
    for image in api web gateway; do
      published="$(head -5 "$work/$image" | paste -sd ' ' -)"
      echo "  $image: ${published:-(none yet)}" >&2
    done
    exit 1
  fi
}

# A node pulls only an image built for its own platform (linux/amd64, linux/arm64): a version built for others would
# never start there (ImagePullBackOff, "no match for platform in manifest"), so this is checked before anything changes.
check_platforms() {
  local nodes image built platform missing=""
  nodes="$(kubectl get nodes \
    -o jsonpath='{range .items[*]}{.status.nodeInfo.operatingSystem}/{.status.nodeInfo.architecture}{"\n"}{end}' | sort -u)"
  for image in api web gateway; do
    built="$("$HERE/versions.sh" "$image" --platforms "$VERSION")"
    while read -r platform; do
      grep -Fxq "$platform" <<<"$built" || missing="$missing $image"
    done <<<"$nodes"
  done
  if [[ -n "$missing" ]]; then
    echo "Version $VERSION cannot run on this cluster: its images are built for $(paste -sd ' ' - <<<"$built")," >&2
    echo "the nodes are $(paste -sd ' ' - <<<"$nodes") (not built for them:$missing). Build the images here instead" >&2
    echo "(clone backend and frontend next to this repository: scripts/deploy.sh local --build), or publish the" >&2
    echo "version for that platform too." >&2
    exit 1
  fi
}

if [[ "$ENVIRONMENT" == local ]] && $deploy_apps; then
  if [[ "$SOURCE" == published ]]; then
    for image in api web gateway; do
      [[ -n "$(registry_image "$image")" ]] \
        || { echo "No newName for product-management/$image in environments/staging/kustomization.yaml" >&2; exit 1; }
    done
    REGISTRY_HOST="$(registry_image api)"
    REGISTRY_HOST="${REGISTRY_HOST%%/*}"
    registry_login "$REGISTRY_HOST"
    choose_version
    check_platforms
    echo "Images: published $VERSION from $REGISTRY_HOST"
  else
    for dir in "$BACKEND" "$FRONTEND"; do
      if [[ ! -d "$dir" ]]; then
        echo "No checkout at $dir to build (--build): clone it next to this repository (or set BACKEND_DIR /" >&2
        echo "FRONTEND_DIR), or run published images: scripts/deploy.sh local --version <X.Y.Z|latest>" >&2
        exit 1
      fi
    done
    BACKEND="$(cd "$BACKEND" && pwd)"
    FRONTEND="$(cd "$FRONTEND" && pwd)"
    echo "Images: built from ${BACKEND_DIR:-../backend} and ${FRONTEND_DIR:-../frontend}"
  fi
fi

# ─── local: images, apply ─────────────────────────────────────────────────────────────────────────────────────────

# Builds the three images from the checkouts (and copies them into k3s); sets api_tag and web_tag.
build_images() {
  if ! docker info >/dev/null 2>&1; then
    echo "Docker is not reachable (docker info): start Docker Desktop, or the Docker Engine on Linux; there, your user" >&2
    echo "must be in the group docker (scripts/setup-ubuntu.sh adds it; log in again or run: newgrp docker)" >&2
    exit 1
  fi
  api_tag="$(image_tag "$BACKEND")"
  web_tag="$(image_tag "$FRONTEND")"
  echo "==> Building product-management/api:$api_tag ($BACKEND)"
  docker build --quiet --tag "product-management/api:$api_tag" "$BACKEND" >/dev/null
  echo "==> Building product-management/web:$web_tag and product-management/gateway:$web_tag ($FRONTEND)"
  # APP_VERSION: the release the web app reports with its browser errors.
  docker build --quiet --tag "product-management/web:$web_tag" --build-arg APP_VERSION="$web_tag" "$FRONTEND" >/dev/null
  docker build --quiet --tag "product-management/gateway:$web_tag" "$FRONTEND/gateway" >/dev/null
  if [[ "$CLUSTER" == k3s ]]; then
    import_into_k3s "product-management/api:$api_tag" "product-management/web:$web_tag" \
      "product-management/gateway:$web_tag"
  fi
}

# environments/local/generated/build/kustomization.yaml: the Kustomize Component environments/local includes, with
# the images of this deploy. Built: their local names and tags. Published: the registry names, the version and the
# pull Secret on every Deployment (only here, so the committed environments/local serves both kinds).
# The tags are quoted: a commit like 1186184 would otherwise be a YAML number, which Kustomize rejects.
write_images_component() {
  local file="$ENV_DIR/generated/build/kustomization.yaml" image
  mkdir -p "$(dirname "$file")"
  {
    if [[ "$SOURCE" == published ]]; then
      echo "# Generated by scripts/deploy.sh local: the published images it runs ($VERSION). Not committed."
    else
      echo "# Generated by scripts/deploy.sh local: the images it built on this machine. Not committed."
    fi
    echo "apiVersion: kustomize.config.k8s.io/v1alpha1"
    echo "kind: Component"
    echo "images:"
    for image in api web gateway; do
      echo "  - name: product-management/$image"
      if [[ "$SOURCE" == published ]]; then
        echo "    newName: $(registry_image "$image")"
        echo "    newTag: \"$VERSION\""
      elif [[ "$image" == api ]]; then
        echo "    newTag: \"$api_tag\""
      else
        echo "    newTag: \"$web_tag\""
      fi
    done
    if [[ "$SOURCE" == published || "${LOGS:-on}" == off ]]; then
      echo "patches:"
    fi
    if [[ "$SOURCE" == published ]]; then
      cat <<'EOF'
  # The registry is private: every Deployment pulls with the Secret gitlab-registry (made by deploy.sh).
  - target: { kind: Deployment }
    patch: |-
      - op: add
        path: /spec/template/spec/imagePullSecrets
        value: [{ name: gitlab-registry }]
EOF
    fi
    if [[ "${LOGS:-on}" == off ]]; then
      cat <<EOF
  # LOGS=off: no log collector, the gateway drops the browser's error reports (answers /collect with 204).
  - patch: |-
      apiVersion: apps/v1
      kind: Deployment
      metadata: { name: gateway, namespace: $NAMESPACE }
      spec:
        template:
          spec:
            containers:
              - name: gateway
                env:
                  - { name: COLLECTOR_UPSTREAM, value: "" }
EOF
    fi
  } > "$file"
}

deploy_local_apps() {
  "$HERE/lib/local-secrets.sh"
  if [[ "$SOURCE" == built ]]; then
    build_images
  fi
  write_images_component

  kubectl apply -f "$ROOT/apps/backend/namespace.yaml" >/dev/null
  if [[ "$SOURCE" == published ]]; then
    echo "==> Secret gitlab-registry: the registry login, to pull the images"
    registry_secret "$REGISTRY_HOST" "$REGISTRY_USER" "$REGISTRY_PASSWORD"
  fi
  ensure_database
  copy_redis_password

  echo "==> Applying environments/local"
  kubectl apply -k "$ENV_DIR"
}

# ─── staging, production: apply what is committed ─────────────────────────────────────────────────────────────────

deploy_remote_apps() {
  local changed="" rendered="$work/rendered.yaml"
  kubectl kustomize "$ENV_DIR" > "$rendered"
  if grep -q ':unreleased$' "$rendered"; then
    echo "environments/$ENVIRONMENT names no version yet for some images (newTag: unreleased):" >&2
    grep ':unreleased$' "$rendered" | sort -u >&2
    echo "Choose released versions first: scripts/set-version.sh $ENVIRONMENT all latest (commit it), or for staging" >&2
    echo "the update:staging job, once backend and frontend have released a version (a vX.Y.Z tag)." >&2
    exit 1
  fi

  kubectl apply -f "$ROOT/apps/backend/namespace.yaml"
  ensure_database

  # The registries are private: the nodes pull with this credential, also long after this job (a pod moved to another
  # node, a restart), so it is a deploy token, not the job's own short-lived token.
  if [[ -n "${CI_DEPLOY_USER:-}" && -n "${CI_DEPLOY_PASSWORD:-}" ]]; then
    echo "==> Secret gitlab-registry: the deploy token, to pull the images"
    registry_secret "${CI_REGISTRY:-registry.gitlab.com}" "$CI_DEPLOY_USER" "$CI_DEPLOY_PASSWORD"
  fi
  require_secret gitlab-registry \
    "set CI_DEPLOY_USER and CI_DEPLOY_PASSWORD (a group deploy token named gitlab-deploy-token, scope read_registry)"

  # The API's own keys. Changing the CI/CD variable and re-running the deploy job is how a key is rotated.
  if [[ -n "${API_SIGNING_KEY:-}" && -n "${API_CONSUMERS:-}" ]]; then
    grep -q -- '-----BEGIN PRIVATE KEY-----' "$API_SIGNING_KEY" \
      || { echo "API_SIGNING_KEY is not a PKCS#8 PEM private key (docker run --rm <api image> generate-signing-key)" >&2; exit 1; }
    jq -e '.Authentication.Consumers | length > 0' "$API_CONSUMERS" >/dev/null \
      || { echo 'API_CONSUMERS must be JSON of the form {"Authentication":{"Consumers":[...]}}' >&2; exit 1; }
    echo "==> Secret api-secrets: from API_SIGNING_KEY and API_CONSUMERS"
    changed="$(kubectl -n "$NAMESPACE" create secret generic api-secrets \
        --from-file=signing-key="$API_SIGNING_KEY" --from-file=consumers.json="$API_CONSUMERS" \
        --dry-run=client -o yaml | apply_secret)"
  fi
  require_secret api-secrets "set API_SIGNING_KEY and API_CONSUMERS (File-type CI/CD variables of the environment)"
  if [[ -n "$changed" ]]; then
    restart_api=true
  fi

  copy_redis_password

  echo "==> Applying environments/$ENVIRONMENT"
  kubectl apply -f "$rendered"
}

# ─── main ─────────────────────────────────────────────────────────────────────────────────────────────────────────

if [[ "$TARGET" == infra || "$TARGET" == all ]]; then
  "$HERE/infra.sh" up
fi

if $deploy_apps; then
  restart_api=false
  if [[ "$ENVIRONMENT" == local ]]; then
    deploy_local_apps
  else
    deploy_remote_apps
  fi
  wait_for_rollouts
  # Pods read their Secrets when they start, and a changed Secret does not change the Deployment.
  if $restart_api; then
    echo "==> A Secret of the API changed: restarting it so it reads it"
    kubectl -n "$NAMESPACE" rollout restart deployment/api
    kubectl -n "$NAMESPACE" rollout status deployment/api --timeout=900s
  fi
  echo
  kubectl -n "$NAMESPACE" get pods -l 'app.kubernetes.io/name in (api, web, gateway)'
  if [[ "$ENVIRONMENT" == local ]]; then
    # Docker Desktop publishes the LoadBalancer on localhost; k3s (ServiceLB) on the node's addresses.
    address="$(kubectl -n "$NAMESPACE" get service gateway \
      -o jsonpath='{.status.loadBalancer.ingress[0].ip}{.status.loadBalancer.ingress[0].hostname}')"
    echo "Application: http://${address:-localhost}:8080"
    echo "Swagger:     kubectl -n $NAMESPACE port-forward deployment/api 5080:8080   →   http://localhost:5080/swagger"
  fi
fi
