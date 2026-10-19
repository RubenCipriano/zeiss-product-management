#!/bin/sh
# Writes the description of a GitHub Release (Markdown, to stdout): the images the version published, with their
# digests, and the commits since the previous version that changed this folder (frontend/). Run by the workflow's
# release job from this folder (POSIX sh); the same script is in backend/ci and frontend/ci.
#
#   ci/release-notes.sh <tag> <image reference>...
#   ci/release-notes.sh frontend/v1.4.0 ghcr.io/rubencipriano/zeiss-product-management/web:1.4.0@sha256:... ghcr.io/rubencipriano/zeiss-product-management/gateway:1.4.0@sha256:...
#
# A tag is <component>/vX.Y.Z (backend/v1.4.0, frontend/v1.4.0: one repository, two release lines). The previous
# version is the newest earlier tag of the same component reachable from this one: for a release X.Y.Z the previous
# release (release candidates are skipped, so 1.4.0 lists everything since 1.3.0), for a release candidate the previous
# tag of any kind. Without one, every commit up to the tag is listed. Needs git with the whole history and the tags
# (actions/checkout with fetch-depth: 0).
set -eu

if [ $# -lt 1 ]; then
  echo "usage: $0 <tag> [<image reference>...]" >&2
  exit 1
fi
tag="$1"
shift
# "backend/" of backend/v1.4.0 (empty for a plain v1.4.0).
prefix="${tag%v[0-9]*}"

case "$tag" in
  *-rc.*) previous="$(git describe --tags --abbrev=0 --match "${prefix}v[0-9]*" "$tag^" 2>/dev/null || true)" ;;
  *) previous="$(git describe --tags --abbrev=0 --match "${prefix}v[0-9]*" --exclude "${prefix}v*-rc.*" "$tag^" 2>/dev/null || true)" ;;
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
# Only the commits that changed this folder: the other component's work is in its own releases.
git log --no-merges --format='- %s (%h, %an)' "$range" -- .
if [ -n "$previous" ] && [ -n "${GITHUB_REPOSITORY:-}" ]; then
  echo
  echo "Full comparison: ${GITHUB_SERVER_URL:-https://github.com}/$GITHUB_REPOSITORY/compare/$previous...$tag"
fi
