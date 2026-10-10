#!/bin/sh
# Writes the description of a GitLab Release (Markdown, to stdout): the images the version published, with their
# digests, and the commits since the previous version. Run by the pipeline's release job (POSIX sh: the release-cli
# image has no bash); the same script is in the backend and the frontend repository.
#
#   ci/release-notes.sh <tag> <image reference>...
#   ci/release-notes.sh v1.4.0 registry.gitlab.com/zeiss/frontend/web:1.4.0@sha256:... registry.gitlab.com/zeiss/frontend/gateway:1.4.0@sha256:...
#
# The previous version is the newest earlier tag v* reachable from this one: for a release X.Y.Z the previous release
# (release candidates are skipped, so 1.4.0 lists everything since 1.3.0), for a release candidate the previous tag of
# any kind. Without one, every commit up to the tag is listed. Needs git with the whole history and the tags
# (GIT_DEPTH 0, git fetch --tags).
set -eu

if [ $# -lt 1 ]; then
  echo "usage: $0 <tag> [<image reference>...]" >&2
  exit 1
fi
tag="$1"
shift

case "$tag" in
  *-rc.*) previous="$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$tag^" 2>/dev/null || true)" ;;
  *) previous="$(git describe --tags --abbrev=0 --match 'v[0-9]*' --exclude 'v*-rc.*' "$tag^" 2>/dev/null || true)" ;;
esac

echo "## Images"
echo
echo "Plain OCI images: they run on Kubernetes, with \`docker run\`, Docker Compose or Swarm (see the repository's README)."
echo
for reference in "$@"; do
  case "$reference" in
    *@*) echo "- \`${reference%@*}\` (digest \`${reference#*@}\`)" ;;
    *) echo "- \`$reference\`" ;;
  esac
done
echo
if [ -n "$previous" ]; then
  echo "## Changes since $previous"
  range="$previous..$tag"
else
  echo "## Changes"
  range="$tag"
fi
echo
git log --no-merges --format='- %s (%h, %an)' "$range"
if [ -n "$previous" ] && [ -n "${CI_PROJECT_URL:-}" ]; then
  echo
  echo "Full comparison: $CI_PROJECT_URL/-/compare/$previous...$tag"
fi
