#!/usr/bin/env bash
# Commits a change of versions to this repository from a workflow: the "pull" half of the release flow (the Backend
# and Frontend workflows only publish versions; deploy/ decides which ones run). It is the same thing Argo CD Image
# Updater's git write-back does, and it is what that tool replaces once Argo CD is in use (README.md). Only the
# workflows run it (scripts/ci/); a person changes a version with scripts/set-version.sh and commits.
#
#   scripts/ci/update-versions.sh staging      # every image to its newest released version, committed to main
#   scripts/ci/update-versions.sh production   # production to the versions staging runs, as a pull request
#
# staging (the workflow update-staging): clones main, runs `scripts/set-version.sh staging all latest` there and, if
# anything changed, commits "staging: api 1.4.0, web 1.4.0, gateway 1.4.0" (the images that changed), pushes it to
# main and writes the file .updated (with that summary) in the current directory. A push made with the workflow's own
# GITHUB_TOKEN starts no workflow, so update-staging deploys what was committed itself when .updated exists. If main
# moved meanwhile, it starts again from the new main (up to 5 times). Nothing new: no commit, no .updated.
#
# production (the manual workflow propose-production): sets production's images to the versions staging runs
# (scripts/set-version.sh production <image> <version>, which checks they exist), pushes the branch
# production/<versions> and opens the pull request "production: api 1.4.0, ..." through GitHub's REST API. Merging it
# is the release decision; the Deploy workflow then applies it. (A person can do the same by hand: set-version.sh
# production ..., commit on a branch, open a pull request.)
#
# This folder is deploy/ of the repository: the files it changes are deploy/environments/... in the clone.
#
# Variables:
#   GITHUB_TOKEN         the workflow's own token (GitHub sets it; the job needs `permissions: contents: write`, and
#                        pull-requests: write for production). It pushes as github-actions[bot], which must be allowed
#                        to push to main (no branch protection on main, or a rule that lets it bypass). Opening the
#                        pull request also needs Settings → Actions → General → "Allow GitHub Actions to create and
#                        approve pull requests".
#   DEPLOY_PUSH_TOKEN    optional, instead of GITHUB_TOKEN: a token with contents (and pull requests) write on THIS
#                        repository, e.g. when main is protected. Neither token is ever printed (a credential helper
#                        reads it).
#   DEPLOY_REPO_URL      default $GITHUB_SERVER_URL/$GITHUB_REPOSITORY.git; a local path or file:// URL for a test
#   DEPLOY_REPO_BRANCH   default main
#   GITHUB_RUN_URL       in the commit message (the workflow sets it)
#   DRY_RUN=1            prepare the commit and print it and the push, push nothing
#   REGISTRY, REGISTRY_USER, REGISTRY_PASSWORD   read the registry (versions.sh)
# Needs: bash, git, curl, jq, yq (all in the workflow's alpine/k8s image).
set -euo pipefail

ENVIRONMENT="${1:-}"
case "$ENVIRONMENT" in staging | production) ;; *) echo "usage: $0 <staging|production>" >&2; exit 1 ;; esac
GITHUB_SERVER="${GITHUB_SERVER_URL:-https://github.com}"
GITHUB_REPO="${GITHUB_REPOSITORY:-RubenCipriano/zeiss-product-management}"
REPO_URL="${DEPLOY_REPO_URL:-$GITHUB_SERVER/$GITHUB_REPO.git}"
BRANCH="${DEPLOY_REPO_BRANCH:-main}"
# Where this folder sits in the repository ("deploy/"), so the same paths work in the clone.
PREFIX="$(git -C "$(dirname "$0")/../.." rev-parse --show-prefix 2>/dev/null || true)"
FILE="${PREFIX}environments/$ENVIRONMENT/kustomization.yaml"
PUSH_TOKEN="${DEPLOY_PUSH_TOKEN:-${GITHUB_TOKEN:-}}"

# Git over HTTPS with a token that stays in the environment: the credential helper reads it when git asks for a
# password, so the token is in no URL, command line, config value or log line.
export GIT_TERMINAL_PROMPT=0
git_auth=()
if [[ "$REPO_URL" == https://* ]]; then
  if [[ -n "$PUSH_TOKEN" ]]; then
    export PUSH_TOKEN
    # shellcheck disable=SC2016 # expanded by the helper's shell, when git calls it
    git_auth=(-c credential.helper= -c 'credential.helper=!f() { echo username=x-access-token; echo "password=$PUSH_TOKEN"; }; f')
  else
    echo "No token to push with: run this in the workflow (GITHUB_TOKEN) or set DEPLOY_PUSH_TOKEN" >&2
    exit 1
  fi
fi
git_() {
  git ${git_auth[@]+"${git_auth[@]}"} -C "$repo" "$@"
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
repo="$work/deploy"
echo "==> Cloning $REPO_URL ($BRANCH)"
git ${git_auth[@]+"${git_auth[@]}"} clone --quiet --depth 20 --branch "$BRANCH" "$REPO_URL" "$repo"
# The author GitHub shows for commits of the workflow's token.
git -C "$repo" config user.name "github-actions[bot]"
git -C "$repo" config user.email "41898282+github-actions[bot]@users.noreply.github.com"

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
  git -C "$repo" commit --quiet -m "$subject" -m "Updated by ${GITHUB_RUN_URL:-scripts/ci/update-versions.sh $ENVIRONMENT}."
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
  rm -f .updated
  for attempt in 1 2 3 4 5; do
    "$repo/${PREFIX}scripts/set-version.sh" staging all latest
    if git -C "$repo" diff --quiet -- "$FILE"; then
      echo "==> Staging already runs the newest released versions: nothing to commit"
      exit 0
    fi
    commit "staging: $(summary)"
    if push origin "HEAD:refs/heads/$BRANCH"; then
      summary_text="$(git -C "$repo" log -1 --format=%s)"
      echo "$summary_text" > .updated
      echo "==> Pushed to $BRANCH: $summary_text (the job deploys it next)"
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

# production: the versions staging runs, as a pull request.
for image in api web gateway; do
  tag="$(IMAGE="product-management/$image" yq '.images[] | select(.name == strenv(IMAGE)) | .newTag' \
    "$repo/${PREFIX}environments/staging/kustomization.yaml")"
  if [[ "$tag" == unreleased ]]; then
    echo "warning: staging runs no version of $image yet; production keeps its own" >&2
    continue
  fi
  "$repo/${PREFIX}scripts/set-version.sh" production "$image" "$tag" >/dev/null
done
if git -C "$repo" diff --quiet -- "$FILE"; then
  echo "==> Production already runs the versions staging runs: nothing to propose"
  exit 0
fi
title="production: $(summary)"
branch="production/$(echo "${title#production: }" | tr ' ,' '-' | tr -s '-')"
commit "$title"
git -C "$repo" --no-pager diff HEAD~1 -- "$FILE"
push --force origin "HEAD:refs/heads/$branch"
if [[ "${DRY_RUN:-0}" == 1 ]]; then
  echo "DRY_RUN: open the pull request \"$title\" ($branch → $BRANCH)"
  exit 0
fi

# The pull request, through GitHub's REST API (curl and jq; the token goes to curl on stdin, never on its command line).
github_api() {
  local method="$1" url="$2"
  shift 2
  printf 'header = "Authorization: Bearer %s"\n' "$PUSH_TOKEN" | curl -sS --fail-with-body -K - -X "$method" \
    -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" "$@" \
    "${GITHUB_API_URL:-https://api.github.com}/repos/$GITHUB_REPO$url"
}
existing="$(github_api GET "/pulls?state=open&head=${GITHUB_REPO%%/*}:$branch" | jq -r '.[0].html_url // empty')"
if [[ -n "$existing" ]]; then
  echo "==> Pull request already open, branch updated: $existing"
  exit 0
fi
body="Sets production to the versions staging runs.

Merging this pull request is the release decision: the Deploy workflow then applies it, once a reviewer of the
environment production approves.

Opened by ${GITHUB_RUN_URL:-scripts/ci/update-versions.sh production}."
url="$(jq -n --arg title "$title" --arg head "$branch" --arg base "$BRANCH" --arg body "$body" \
    '{title: $title, head: $head, base: $base, body: $body}' \
  | github_api POST /pulls --data @- | jq -r .html_url)"
echo "==> Pull request \"$title\" ($branch → $BRANCH): $url"
echo "    merging it releases to production (the Deploy workflow, after the environment's approval)"
