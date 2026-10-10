#!/usr/bin/env bash
# Commits a change of versions to this repository from the pipeline: the "pull" half of the release flow (the app
# pipelines only publish versions; this repository decides which ones run). It is the same thing Argo CD Image
# Updater's git write-back does, and it is what that tool replaces once Argo CD is in use (README.md).
#
#   scripts/update-versions.sh staging      # every image to its newest released version, committed to main
#   scripts/update-versions.sh production   # production to the versions staging runs, as a merge request
#
# staging (the scheduled job update:staging): clones main, runs `scripts/set-version.sh staging all latest` there and,
# if anything changed, commits "staging: api 1.4.0, web 1.4.0, gateway 1.4.0" (the images that changed) and pushes it
# to main. That push starts this repository's pipeline, whose deploy:staging migrates, applies and smoke-checks
# staging. If main moved meanwhile, it starts again from the new main (up to 5 times). Nothing new: no commit.
#
# production (the manual job propose:production): sets production's images to the versions staging runs
# (scripts/set-version.sh production <image> <version>, which checks they exist), pushes the branch
# production/<versions> and opens the merge request "production: api 1.4.0, ..." with git push options. Merging it is
# the release decision; deploy:production then applies it. (A person can do the same by hand: set-version.sh
# production ..., commit on a branch, open a merge request.)
#
# Variables:
#   DEPLOY_PUSH_TOKEN    a project access token of THIS repository, role Maintainer (main is protected), scope
#                        write_repository. Masked and protected; never printed (a git credential helper reads it).
#   DEPLOY_REPO_URL      default $CI_SERVER_URL/$CI_PROJECT_PATH.git (https://gitlab.com/zeiss/deploy.git); a local
#                        path or file:// URL for a test
#   DEPLOY_REPO_BRANCH   default main
#   GITLAB_USER_NAME, GITLAB_USER_EMAIL   the commit's author: who owns the schedule or ran the job (GitLab sets them)
#   CI_PIPELINE_URL      in the commit message (GitLab sets it)
#   DRY_RUN=1            prepare the commit and print it and the push, push nothing
#   REGISTRY, REGISTRY_USER, REGISTRY_PASSWORD, CI_DEPLOY_USER, CI_DEPLOY_PASSWORD   read the registry (versions.sh)
# Needs: bash, git, curl, jq, yq (all in the pipeline's alpine/k8s image).
set -euo pipefail

ENVIRONMENT="${1:-}"
case "$ENVIRONMENT" in staging | production) ;; *) echo "usage: $0 <staging|production>" >&2; exit 1 ;; esac
REPO_URL="${DEPLOY_REPO_URL:-${CI_SERVER_URL:-https://gitlab.com}/${CI_PROJECT_PATH:-zeiss/deploy}.git}"
BRANCH="${DEPLOY_REPO_BRANCH:-main}"
FILE="environments/$ENVIRONMENT/kustomization.yaml"

# Git over HTTPS with the token, which stays in the environment: the credential helper reads $DEPLOY_PUSH_TOKEN when
# git asks for a password, so the token is in no URL, command line, config value or log line.
export GIT_TERMINAL_PROMPT=0
git_auth=()
if [[ "$REPO_URL" == https://* ]]; then
  : "${DEPLOY_PUSH_TOKEN:?DEPLOY_PUSH_TOKEN is not set (a project access token of this repository, write_repository)}"
  # shellcheck disable=SC2016 # expanded by the helper's shell, when git calls it
  git_auth=(-c credential.helper= -c 'credential.helper=!f() { echo username=oauth2; echo "password=$DEPLOY_PUSH_TOKEN"; }; f')
fi
git_() {
  git ${git_auth[@]+"${git_auth[@]}"} -C "$repo" "$@"
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
repo="$work/deploy"
echo "==> Cloning $REPO_URL ($BRANCH)"
git ${git_auth[@]+"${git_auth[@]}"} clone --quiet --depth 20 --branch "$BRANCH" "$REPO_URL" "$repo"
git -C "$repo" config user.name "${GITLAB_USER_NAME:-update-versions}"
git -C "$repo" config user.email "${GITLAB_USER_EMAIL:-update-versions@localhost}"

# "api 1.4.0, web 1.4.0, gateway 1.4.0": the images whose newTag differs from the last commit.
summary() {
  local image before after text=""
  for image in api web gateway; do
    before="$(git -C "$repo" show "HEAD:$FILE" | IMAGE="product-management/$image" yq '.images[] | select(.name == strenv(IMAGE)) | .newTag')"
    after="$(IMAGE="product-management/$image" yq '.images[] | select(.name == strenv(IMAGE)) | .newTag' "$repo/$FILE")"
    [[ "$before" == "$after" ]] || text="${text:+$text, }$image $after"
  done
  echo "$text"
}

commit() {
  local subject="$1"
  git -C "$repo" add "$FILE"
  git -C "$repo" commit --quiet -m "$subject" -m "Updated by ${CI_PIPELINE_URL:-scripts/update-versions.sh $ENVIRONMENT}."
  git -C "$repo" --no-pager show --stat --format='%h %s%n%b' HEAD
}

push() {
  if [[ "${DRY_RUN:-0}" == 1 ]]; then
    echo "DRY_RUN: git push $*"
    return 0
  fi
  git_ push --quiet "$@"
}

if [[ "$ENVIRONMENT" == staging ]]; then
  for attempt in 1 2 3 4 5; do
    "$repo/scripts/set-version.sh" staging all latest
    if git -C "$repo" diff --quiet -- "$FILE"; then
      echo "==> Staging already runs the newest released versions: nothing to commit"
      exit 0
    fi
    commit "staging: $(summary)"
    if push origin "HEAD:refs/heads/$BRANCH"; then
      echo "==> Pushed to $BRANCH: its pipeline deploys staging (deploy:staging)"
      exit 0
    fi
    # Someone pushed first (a person, or a merge): start again from the new main.
    echo "==> $BRANCH moved (attempt $attempt): starting again from the latest $BRANCH"
    sleep $((attempt * 2))
    git_ fetch --quiet origin "$BRANCH"
    git -C "$repo" reset --quiet --hard "origin/$BRANCH"
  done
  echo "Could not push to $BRANCH after 5 attempts" >&2
  exit 1
fi

# production: the versions staging runs, as a merge request.
for image in api web gateway; do
  tag="$(IMAGE="product-management/$image" yq '.images[] | select(.name == strenv(IMAGE)) | .newTag' \
    "$repo/environments/staging/kustomization.yaml")"
  if [[ "$tag" == unreleased ]]; then
    echo "warning: staging runs no version of $image yet; production keeps its own" >&2
    continue
  fi
  "$repo/scripts/set-version.sh" production "$image" "$tag" >/dev/null
done
if git -C "$repo" diff --quiet -- "$FILE"; then
  echo "==> Production already runs the versions staging runs: nothing to propose"
  exit 0
fi
title="production: $(summary)"
branch="production/$(echo "${title#production: }" | tr ' ,' '-' | tr -s '-')"
commit "$title"
git -C "$repo" --no-pager diff HEAD~1 -- "$FILE"
push --force origin "HEAD:refs/heads/$branch" \
  -o merge_request.create -o "merge_request.target=$BRANCH" -o "merge_request.title=$title" \
  -o merge_request.remove_source_branch
echo "==> Merge request \"$title\" ($branch → $BRANCH): merging it releases to production (deploy:production)"
