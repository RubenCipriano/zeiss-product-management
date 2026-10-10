#!/usr/bin/env bash
# Releases this pipeline's images to an environment by changing their tags in the deploy repository (GitOps). It
# never applies anything to a cluster: the deploy repository's pipeline (or, later, Argo CD) applies what is committed
# there. The same script is in the backend and the frontend repository.
#
#   ci/release.sh staging    <image>=<tag> [...]   # commit to the deploy repository's main and push
#   ci/release.sh production <image>=<tag> [...]   # push a branch and open a merge request into main
#
#   ci/release.sh staging product-management/api="$CI_COMMIT_SHORT_SHA"
#   ci/release.sh production product-management/web="$CI_COMMIT_SHORT_SHA" product-management/gateway="$CI_COMMIT_SHORT_SHA"
#
# <image> is an images[].name in environments/<environment>/kustomization.yaml; its newTag becomes <tag>. Staging is
# one commit per release on main ("backend: api <tag>"), so the deploy repository's history is the deployment history
# and `git revert` is the rollback. If main moved meanwhile (the other app released at the same moment), it starts
# again from the new main, up to 5 times. Production is a merge request: merging it is the release decision.
#
# Variables:
#   DEPLOY_REPO_TOKEN    a project access token of the deploy repository with the scope write_repository and the role
#                        Maintainer (it pushes to the protected main). Masked and protected; never printed.
#   DEPLOY_REPO_URL      default https://gitlab.com/zeiss/deploy.git (a local path or file:// URL for a test)
#   DEPLOY_REPO_BRANCH   default main
#   GITLAB_USER_NAME, GITLAB_USER_EMAIL   the commit's author: who ran the pipeline (GitLab sets them)
#   CI_PROJECT_NAME, CI_PIPELINE_URL, CI_PROJECT_URL, CI_COMMIT_SHA   the commit message (GitLab sets them)
#   DRY_RUN=1            prepare the commit, print it and the push command, push nothing
# Needs git and yq (both in the alpine/k8s image).
set -euo pipefail

usage() {
  echo "usage: $0 <staging|production> <image>=<tag> [<image>=<tag> ...]" >&2
  exit 1
}

ENVIRONMENT="${1:-}"
case "$ENVIRONMENT" in staging | production) ;; *) usage ;; esac
shift
[[ $# -gt 0 ]] || usage
REPO_URL="${DEPLOY_REPO_URL:-https://gitlab.com/zeiss/deploy.git}"
BRANCH="${DEPLOY_REPO_BRANCH:-main}"
FILE="environments/$ENVIRONMENT/kustomization.yaml"
PROJECT="${CI_PROJECT_NAME:-$(basename "$PWD")}"

# What is released: image names, tags, and a short summary for the commit ("api 1a2b3c4", "web, gateway 1a2b3c4").
names=()
tags=()
for pair in "$@"; do
  [[ "$pair" == ?*=?* ]] || usage
  names+=("${pair%%=*}")
  tags+=("${pair#*=}")
done
summary=""
for i in "${!names[@]}"; do
  short="${names[$i]##*/}"
  if [[ "${tags[$i]}" == "${tags[0]}" ]]; then
    summary="${summary:+$summary, }$short"
  else
    summary="${summary:+$summary, }$short ${tags[$i]}"
  fi
done
summary="$summary ${tags[0]}"

# Git over HTTPS with the token, which stays in the environment: the credential helper reads $DEPLOY_REPO_TOKEN when
# git asks for a password, so the token is in no URL, command line, config value or log line.
export GIT_TERMINAL_PROMPT=0
git_auth=()
if [[ "$REPO_URL" == https://* ]]; then
  : "${DEPLOY_REPO_TOKEN:?DEPLOY_REPO_TOKEN is not set (a project access token of the deploy repository, write_repository)}"
  # shellcheck disable=SC2016 # expanded by the helper's shell, when git calls it
  git_auth=(-c credential.helper= -c 'credential.helper=!f() { echo username=oauth2; echo "password=$DEPLOY_REPO_TOKEN"; }; f')
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
repo="$work/deploy"
echo "==> Cloning the deploy repository ($REPO_URL, $BRANCH)"
git ${git_auth[@]+"${git_auth[@]}"} clone --quiet --depth 20 --branch "$BRANCH" "$REPO_URL" "$repo"
git -C "$repo" config user.name "${GITLAB_USER_NAME:-release}"
git -C "$repo" config user.email "${GITLAB_USER_EMAIL:-release@localhost}"

# Sets each image's newTag in the environment's kustomization.yaml; fails on a name that is not there.
set_tags() {
  local i
  [[ -f "$repo/$FILE" ]] || { echo "No $FILE in the deploy repository" >&2; exit 1; }
  for i in "${!names[@]}"; do
    NAME="${names[$i]}" TAG="${tags[$i]}" yq -e '.images[] | select(.name == strenv(NAME))' "$repo/$FILE" >/dev/null 2>&1 \
      || { echo "No image ${names[$i]} in $FILE of the deploy repository" >&2; exit 1; }
    NAME="${names[$i]}" TAG="${tags[$i]}" \
      yq -i '(.images[] | select(.name == strenv(NAME))).newTag = strenv(TAG)' "$repo/$FILE"
  done
}

commit() {
  local subject="$1"
  git -C "$repo" add "$FILE"
  git -C "$repo" commit --quiet -m "$subject" \
    -m "Released by ${CI_PIPELINE_URL:-a manual run}${CI_COMMIT_SHA:+ from ${CI_PROJECT_URL:-$PROJECT}/-/commit/$CI_COMMIT_SHA}."
  git -C "$repo" --no-pager show --stat --format='%h %s%n%b' HEAD
  git -C "$repo" --no-pager diff HEAD~1 -- "$FILE"
}

push() {
  if [[ "${DRY_RUN:-0}" == 1 ]]; then
    echo "DRY_RUN: git push $*"
    return 0
  fi
  git -C "$repo" ${git_auth[@]+"${git_auth[@]}"} push --quiet "$@"
}

if [[ "$ENVIRONMENT" == staging ]]; then
  for attempt in 1 2 3 4 5; do
    set_tags
    if git -C "$repo" diff --quiet -- "$FILE"; then
      echo "==> $FILE already has these tags: nothing to release"
      exit 0
    fi
    commit "$PROJECT: $summary"
    if push origin "HEAD:refs/heads/$BRANCH"; then
      echo "==> Released $summary to staging: the deploy repository's pipeline applies it"
      exit 0
    fi
    # Someone pushed first (most likely the other app's release): start again from the new main.
    echo "==> $BRANCH moved (attempt $attempt): starting again from the deploy repository's latest $BRANCH"
    sleep $((attempt * 2))
    git -C "$repo" ${git_auth[@]+"${git_auth[@]}"} fetch --quiet origin "$BRANCH"
    git -C "$repo" reset --quiet --hard "origin/$BRANCH"
  done
  echo "Could not push to $BRANCH after 5 attempts" >&2
  exit 1
fi

# production: a branch with the change and a merge request into main, opened by GitLab from the push options.
set_tags
if git -C "$repo" diff --quiet -- "$FILE"; then
  echo "==> Production already runs these tags: nothing to release"
  exit 0
fi
release_branch="release/production/$PROJECT-${tags[0]}"
title="Release $summary to production"
commit "$PROJECT: $summary to production"
push --force origin "HEAD:refs/heads/$release_branch" \
  -o merge_request.create -o "merge_request.target=$BRANCH" -o "merge_request.title=$title" \
  -o merge_request.remove_source_branch
echo "==> Merge request \"$title\" ($release_branch → $BRANCH) in the deploy repository: merging it releases to"
echo "    production (its pipeline then offers deploy:production)"
