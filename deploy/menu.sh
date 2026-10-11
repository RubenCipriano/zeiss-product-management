#!/usr/bin/env bash
# The deploy repository's menu: the common tasks, numbered, so nobody has to remember the scripts. Each item asks only
# what it needs, shows the exact command it is about to run, then runs it. Every item is a plain script too
# (scripts/), so nothing here is needed to automate anything.
#
#   ./menu.sh           the menu, on the current kubectl context
#   ./menu.sh --help    the items and the commands behind them
#
# Variables reach the scripts unchanged, e.g. LOGS=off ./menu.sh or KUBECONFIG=<file> ./menu.sh. A registry token
# typed here is read without echo, kept in this process only and handed to the one command that needs it, in its
# environment (never on a command line, never in a file). Ctrl-C stops the running command and returns to the menu;
# at the menu's prompt it leaves the menu.
#
# Plain bash 3.2 or later (macOS, Linux, WSL): read and printf, no dialog or whiptail.
set -uo pipefail   # no -e: a failed command returns to the menu

HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$HERE" || exit 1
NAMESPACE=product-management
# The registry login for published images: the environment's, or what is typed in item 3, 4 or 5.
registry_user="${REGISTRY_USER:-${CI_DEPLOY_USER:-}}"
registry_password="${REGISTRY_PASSWORD:-${CI_DEPLOY_PASSWORD:-}}"
answer=""

usage() {
  cat <<'EOF'
Usage: ./menu.sh [--help]

The deploy repository's menu, on the current kubectl context. Each item shows the command it runs:

  1  Prepare this Ubuntu server        scripts/setup-ubuntu.sh                          (Ubuntu only)
  2  Shared infrastructure             scripts/infra.sh up | status | down [--delete-data]
  3  Deploy the application            scripts/deploy.sh local --version <X.Y.Z|latest> (published images)
                                       scripts/deploy.sh local --build                  (from ../backend, ../frontend)
                                       staging, production: the pipeline (item 5 changes their versions)
  4  Published versions                scripts/versions.sh all
  5  Change an environment's version   scripts/set-version.sh <staging|production> <image> <version|latest>
  6  Add a node                        scripts/add-node.sh <user@host> <database|cache|apps> [--dry-run]
  7  Status                            kubectl get pods,pvc,services (product-management, infra) and the URLs
  8  Smoke check                       scripts/ci/smoke.sh <application URL>
  9  Start over                        delete the application and the infrastructure with all data, deploy again
  0  Exit

The status line above the items shows the kubectl context and its kind, whether the infrastructure runs, the api, web
and gateway versions in product-management and the application's URL.
Published images need a registry login: REGISTRY_USER and REGISTRY_PASSWORD (a deploy token with read_registry), or
CI_DEPLOY_USER and CI_DEPLOY_PASSWORD, or docker login registry.gitlab.com; otherwise the menu asks for it.
EOF
}

case "${1:-}" in
  "") ;;
  -h | --help) usage; exit 0 ;;
  *) usage >&2; exit 1 ;;
esac

# ─── input and output ─────────────────────────────────────────────────────────────────────────────────────────────

say() {
  printf '%s\n' "$*"
}

# Leaves the menu at the end of the input (Ctrl-D, or the last line of piped input).
bye() {
  echo
  say "Bye."
  exit 0
}

# ask <question> [default]: one line into $answer, the default when it is empty.
ask() {
  local question="$1" default="${2:-}"
  if [[ -n "$default" ]]; then
    printf '%s [%s]: ' "$question" "$default"
  else
    printf '%s: ' "$question"
  fi
  read -r answer || bye
  # Piped input is not echoed by a terminal: show it, so the transcript reads like a session.
  [[ -t 0 ]] || say "$answer"
  answer="${answer:-$default}"
}

# ask_hidden <question>: like ask, without echoing what is typed (a token).
ask_hidden() {
  printf '%s: ' "$1"
  read -r -s answer || bye
  echo
}

# confirm <question>: true for y or yes; no is the default. confirm_default: yes is the default.
confirm() {
  ask "$1 (y/N)"
  [[ "$answer" == [yY] || "$answer" == [yY][eE][sS] ]]
}
confirm_default() {
  ask "$1 (Y/n)"
  [[ "$answer" != [nN] && "$answer" != [nN][oO] ]]
}

# The command as it will run, for the screen (an argument with spaces in quotes); the registry token is never shown.
shown() {
  local arg line=""
  if [[ "$1" == with_registry ]]; then
    shift
    # No user: the scripts use this machine's docker login.
    [[ -z "$registry_user" ]] || line="REGISTRY_USER=$registry_user REGISTRY_PASSWORD=<hidden>"
  fi
  for arg in "$@"; do
    if [[ ! "$arg" =~ ^[A-Za-z0-9_./:=,@%+-]+$ ]]; then
      arg="\"$arg\""
    fi
    line="${line:+$line }$arg"
  done
  printf '%s' "$line"
}

# run <command...>: shows the command, runs it, says how it ended. Ctrl-C stops the command, not the menu.
run() {
  local status
  printf '\n$ %s\n\n' "$(shown "$@")"
  interrupted=false
  trap 'interrupted=true' INT
  "$@"
  status=$?
  trap on_interrupt INT
  if $interrupted; then
    echo
    say "(interrupted)"
    return 130
  elif [[ $status != 0 ]]; then
    say "(the command failed: exit $status)"
  fi
  return $status
}

on_interrupt() {
  bye
}
trap on_interrupt INT

# with_registry <command...>: the command with the registry login in its environment only (the menu exports nothing).
with_registry() {
  REGISTRY_USER="$registry_user" REGISTRY_PASSWORD="$registry_password" "$@"
}

# Asks for the registry login if none is set (or uses the machine's docker login when the user is left empty).
ask_registry_login() {
  if [[ -n "$registry_user" && -n "$registry_password" ]]; then
    return 0
  fi
  say "The registry (registry.gitlab.com) is private: a deploy token with the scope read_registry is needed"
  say "(GitLab: group zeiss → Settings → Repository → Deploy tokens, e.g. one named server-pull)."
  ask "Registry user, the deploy token's username (empty: this machine's docker login)"
  if [[ -z "$answer" ]]; then
    return 0
  fi
  registry_user="$answer"
  ask_hidden "Deploy token (not shown)"
  registry_password="$answer"
  if [[ -z "$registry_password" ]]; then
    say "No token given."
    registry_user=""
    return 1
  fi
}

# ─── the status line ──────────────────────────────────────────────────────────────────────────────────────────────

# kubectl that gives up after 5 seconds and prints no errors (the status line must never hang or shout).
k() {
  kubectl --request-timeout=5s "$@" 2>/dev/null
}

has_checkouts() {
  [[ -d "${BACKEND_DIR:-$HERE/../backend}" && -d "${FRONTEND_DIR:-$HERE/../frontend}" ]]
}

is_ubuntu() {
  [[ "$(uname -s)" == Linux && -r /etc/os-release ]] && grep -q '^ID=ubuntu$' /etc/os-release
}

# Sets context, cluster (a description), local_cluster (true for a cluster on this machine), infra, application
# and app_url.
refresh_status() {
  local versions nodes pods running images image name tag kinds="" address
  context="" cluster="" local_cluster=false infra="" application="" app_url=""
  if ! command -v kubectl >/dev/null; then
    cluster="kubectl is not installed (item 1 on Ubuntu; Docker Desktop elsewhere)"
    return
  fi
  if ! context="$(kubectl config current-context 2>/dev/null)"; then
    cluster="no kubectl context (KUBECONFIG?)"
    return
  fi
  if ! versions="$(k get nodes -o jsonpath='{.items[*].status.nodeInfo.kubeletVersion}')"; then
    cluster="$context (not reachable)"
    return
  fi
  nodes=" $(k get nodes -o jsonpath='{.items[*].metadata.name}') "
  if [[ "$context" == docker-desktop ]]; then
    cluster="$context (Docker Desktop)"
    local_cluster=true
  elif [[ "$versions" == *+k3s* && "$nodes" == *" $(hostname | tr '[:upper:]' '[:lower:]') "* ]]; then
    cluster="$context (k3s on this machine)"
    local_cluster=true
  elif [[ "$versions" == *+k3s* ]]; then
    cluster="$context (k3s)"
  else
    cluster="$context"
  fi

  pods="$(k -n infra get pods --no-headers | grep -v -c Completed)"
  running="$(k -n infra get pods --no-headers | grep -c ' Running ')"
  if [[ "${pods:-0}" == 0 ]]; then
    infra="not running (item 2)"
  else
    infra="$running of $pods pods running"
  fi

  images="$(k -n "$NAMESPACE" get deployment api web gateway \
    -o jsonpath='{range .items[*]}{.metadata.name} {.spec.template.spec.containers[0].image}{"\n"}{end}')"
  if [[ -z "$images" ]]; then
    application="not deployed (item 3)"
  else
    while read -r name image; do
      tag="${image##*:}"
      application="${application:+$application, }$name $tag"
      case "$image" in
        product-management/*) kinds="$kinds built" ;;
        *) kinds="$kinds published" ;;
      esac
    done <<<"$images"
    case "$kinds" in
      *built*published* | *published*built*) application="$application (built and published)" ;;
      *built*) application="$application (built here)" ;;
      *) application="$application (published)" ;;
    esac
    # Docker Desktop publishes the LoadBalancer on localhost; k3s (ServiceLB) on the node's addresses.
    address="$(k -n "$NAMESPACE" get service gateway \
      -o jsonpath='{.status.loadBalancer.ingress[0].ip}{.status.loadBalancer.ingress[0].hostname}')"
    app_url="http://${address:-localhost}:8080"
  fi
}

show_menu() {
  local ubuntu_note="" checkout_note=""
  is_ubuntu || ubuntu_note="   (Ubuntu only)"
  echo
  say "Product Management: deploy menu"
  say "  Cluster:      ${cluster}"
  if [[ -n "$context" ]]; then
    say "  Infra:        ${infra:-?}"
    say "  Application:  ${application:-?}"
    say "  URL:          ${app_url:-none yet}"
  fi
  has_checkouts || checkout_note=" (published versions: no ../backend, ../frontend here)"
  echo
  say "  1) Prepare this Ubuntu server$ubuntu_note"
  say "  2) Shared infrastructure: up, status, stop"
  say "  3) Deploy the application$checkout_note"
  say "  4) Published versions"
  say "  5) Change an environment's version"
  say "  6) Add a node"
  say "  7) Status"
  say "  8) Smoke check"
  say "  9) Start over (deletes all data)"
  say "  0) Exit"
}

# ─── the items ────────────────────────────────────────────────────────────────────────────────────────────────────

item_setup_ubuntu() {
  if ! is_ubuntu; then
    say "Only on an Ubuntu server (22.04, 24.04): this machine is $(uname -s). On macOS and Windows, enable"
    say "Kubernetes in Docker Desktop instead (README.md, Where it runs)."
    return
  fi
  say "Installs Docker Engine, kubectl and a single-node k3s at pinned versions (uses sudo; safe to run again)."
  confirm "Run it" || return
  run scripts/setup-ubuntu.sh || return
  say "You were added to the group docker: run 'newgrp docker' (or log in again) before building images here."
}

item_infra() {
  say "The shared infrastructure, namespace infra (PostgreSQL, Redis, logs), in context ${context:-?}:"
  say "  1) up      start it, or apply changes (keeps the data)"
  say "  2) status  pods, services, volumes, how to reach them"
  say "  3) stop    remove it; the data is kept unless you ask"
  say "  0) back"
  ask "Choose" 0
  case "$answer" in
    1) run scripts/infra.sh up ;;
    2) run scripts/infra.sh status ;;
    3) stop_infra ;;
  esac
}

stop_infra() {
  local components=() component which
  ask "Which components (postgres redis logs)" all
  which="$answer"
  if [[ "$which" != all ]]; then
    read -r -a components <<<"$which"
    for component in ${components[@]+"${components[@]}"}; do
      case "$component" in
        postgres | redis | logs) ;;
        *) say "Unknown component: $component (postgres, redis, logs)"; return ;;
      esac
    done
  fi
  confirm "Stop $which in context $context? (the data is kept)" || { say "Nothing stopped."; return; }
  if confirm "Also DELETE their data: volumes and passwords, every database row and log line?"; then
    ask "This cannot be undone. Type the context's name ($context) to delete the data"
    if [[ "$answer" != "$context" ]]; then
      say "Not confirmed: nothing was stopped or deleted."
      return
    fi
    run scripts/infra.sh down ${components[@]+"${components[@]}"} --delete-data
  else
    run scripts/infra.sh down ${components[@]+"${components[@]}"}
  fi
}

# Asks for a version and the registry login, then deploys the published images to the local environment.
deploy_published() {
  ask "Version: X.Y.Z, or latest for the newest release (item 4 lists them)" latest
  local version="$answer"
  ask_registry_login || return
  run with_registry scripts/deploy.sh local --version "$version"
}

item_deploy() {
  say "Deploy the application (namespace product-management) to context ${context:-?}:"
  say "  1) local, a published version   the released images from registry.gitlab.com (a deploy token is needed)"
  if has_checkouts; then
    say "  2) local, built from source     images built from your checkouts ../backend and ../frontend"
  else
    say "  2) local, built from source     not here: no ../backend and ../frontend next to this repository"
  fi
  say "  3) staging or production"
  say "  0) back"
  ask "Choose" 0
  case "$answer" in
    1) deploy_published ;;
    2)
      if has_checkouts; then
        run scripts/deploy.sh local --build
      else
        say "Clone backend and frontend next to this repository to build from source (README.md, Where the images"
        say "come from); or deploy a published version (1)."
      fi
      ;;
    3) explain_remote ;;
  esac
}

explain_remote() {
  say "staging and production are deployed by this repository's pipeline (gitlab.com/zeiss/deploy), not from here:"
  say "they run the versions committed in environments/<staging|production>/kustomization.yaml. To change them,"
  say "change the version (item 5) and commit it: staging on main, production through a merge request. The pipeline"
  say "then deploys (deploy:staging by itself, deploy:production when a person starts it)."
  confirm_default "Change a version now (item 5)?" && item_set_version
}

item_versions() {
  ask_registry_login || return
  run with_registry scripts/versions.sh all
}

item_set_version() {
  local environment image version
  if ! command -v yq >/dev/null; then
    say "set-version.sh needs yq: brew install yq (macOS), sudo snap install yq (Ubuntu)."
    return
  fi
  ask "Environment (staging or production)" staging
  environment="$answer"
  case "$environment" in staging | production) ;; *) say "staging or production"; return ;; esac
  ask "Image (api, web, gateway; frontend = web and gateway; all)" all
  image="$answer"
  case "$image" in api | web | gateway | frontend | all) ;; *) say "api, web, gateway, frontend or all"; return ;; esac
  ask "Version: X.Y.Z, or latest" latest
  version="$answer"
  ask_registry_login || return
  run with_registry scripts/set-version.sh "$environment" "$image" "$version" || return
  echo
  if [[ "$environment" == staging ]]; then
    say "Next: commit environments/staging/kustomization.yaml and push it to main (or open a merge request);"
    say "the pipeline deploys it (deploy:staging). While update:staging's schedule runs, it moves staging to the"
    say "newest version again: pause the schedule to hold an older one."
  else
    say "Next: commit environments/production/kustomization.yaml on a branch and open a merge request. Merging it"
    say "is the release decision; then start deploy:production in the pipeline."
  fi
}

item_add_node() {
  local node role args
  say "Joins a machine to this k3s cluster over ssh (it needs sudo there), as a node for a role."
  ask "The machine (user@host)"
  node="$answer"
  [[ "$node" == *@* ]] || { say "Give it as user@host."; return; }
  ask "Its role: database (PostgreSQL only), cache (Redis only) or apps" apps
  role="$answer"
  case "$role" in database | cache | apps) ;; *) say "database, cache or apps"; return ;; esac
  args=("$node" "$role")
  ask "The k3s server's API, https://<server>:6443 (empty: the current context's server)"
  if [[ -n "$answer" ]]; then
    args+=(--server "$answer")
  fi
  if confirm_default "First a dry run (prints every step, changes nothing)?"; then
    run scripts/add-node.sh "${args[@]}" --dry-run || return
  fi
  confirm "Run it for real (installs the k3s agent on $node)?" || return
  run scripts/add-node.sh "${args[@]}"
}

item_status() {
  run kubectl -n "$NAMESPACE" get pods,pvc,services
  run kubectl -n infra get pods,pvc,services
  echo
  say "Application  ${app_url:-not deployed yet (item 3)}"
  say "Grafana      kubectl -n infra port-forward svc/grafana 3000:3000                  → http://localhost:3000"
  say "Swagger      kubectl -n $NAMESPACE port-forward deployment/api 5080:8080   → http://localhost:5080/swagger"
}

item_smoke() {
  ask "The application's URL" "${app_url:-http://localhost:8080}"
  run scripts/ci/smoke.sh "$answer"
}

item_start_over() {
  local source=published version=latest deploy=()
  if ! $local_cluster; then
    say "Start over is for a cluster on this machine (Docker Desktop, or k3s on this server); the context"
    say "${context:-?} is not one. Nothing was deleted."
    return
  fi
  if has_checkouts; then
    say "Afterwards, deploy:  1) a published version   2) built from ../backend and ../frontend"
    ask "Choose" 2
    [[ "$answer" == 1 ]] || source=built
  fi
  if [[ "$source" == published ]]; then
    ask "Version: X.Y.Z, or latest" latest
    version="$answer"
    ask_registry_login || return
    deploy=(with_registry scripts/deploy.sh local --version "$version")
  else
    deploy=(scripts/deploy.sh local --build)
  fi
  say "This deletes, in context $context: the namespace product-management (the application and its Secrets), the"
  say "shared infrastructure WITH ITS DATA (every database row, every log line, the passwords) and"
  say "environments/local/generated (the local keys), then deploys from nothing:"
  say "  kubectl delete namespace product-management --ignore-not-found"
  say "  scripts/infra.sh down --delete-data"
  say "  rm -rf environments/local/generated"
  say "  scripts/infra.sh up"
  say "  $(shown "${deploy[@]}")"
  say "  scripts/ci/smoke.sh <the application's URL>"
  ask "To go on, type: delete everything"
  if [[ "$answer" != "delete everything" ]]; then
    say "Not confirmed: nothing was deleted."
    return
  fi
  run kubectl delete namespace "$NAMESPACE" --ignore-not-found || return
  run scripts/infra.sh down --delete-data || return
  run rm -rf environments/local/generated || return
  run scripts/infra.sh up || return
  run "${deploy[@]}" || return
  refresh_status
  run scripts/ci/smoke.sh "${app_url:-http://localhost:8080}"
}

# ─── main ─────────────────────────────────────────────────────────────────────────────────────────────────────────

while true; do
  refresh_status
  show_menu
  ask "Choose"
  case "$answer" in
    1) item_setup_ubuntu ;;
    2) item_infra ;;
    3) item_deploy ;;
    4) item_versions ;;
    5) item_set_version ;;
    6) item_add_node ;;
    7) item_status ;;
    8) item_smoke ;;
    9) item_start_over ;;
    0 | q | exit) say "Bye."; exit 0 ;;
    "") ;;
    *) say "No item \"$answer\": choose 0 to 9." ;;
  esac
done
