#!/usr/bin/env bash
# Smoke check of a deployed environment, from outside, through its public URL (the gateway). Run by the pipeline
# after every deployment; works from a laptop too.
#
#   scripts/smoke.sh https://staging.products.example.com
#   scripts/smoke.sh http://localhost:8080
#
# Checks, retrying each for up to a minute (the gateway may still be picking up new API pods):
#   1. GET  /healthz           200: the gateway is up (behind the Ingress, with TLS);
#   2. GET  /                  200 with the web app's index.html: the gateway reaches the web app;
#   3. POST /api/auth/token    with an unknown client, 401 invalid_client: the gateway reaches the API and the API
#                              answers (the API's /health/ready is not routed through the gateway; its readiness was
#                              already awaited by the rollout);
#   4. with SMOKE_CLIENT_ID and SMOKE_CLIENT_SECRET (a viewer consumer of that environment): signs in and reads
#      GET /api/categories, 200: tokens, the edge's token check, the API and the database end to end.
# Needs curl.
set -euo pipefail

BASE_URL="${1:?usage: $0 <base url, e.g. https://staging.products.example.com>}"
BASE_URL="${BASE_URL%/}"
failed=0

# check <description> <expected status> <expected text in the body, or ""> <curl arguments...>
check() {
  local description="$1" status="$2" text="$3" body code attempt
  shift 3
  body="$(mktemp)"
  for attempt in 1 2 3 4 5 6; do
    code="$(curl -sS -o "$body" -w '%{http_code}' --max-time 10 "$@" || echo 000)"
    if [[ "$code" == "$status" ]] && { [[ -z "$text" ]] || grep -q -- "$text" "$body"; }; then
      echo "  [PASS] $description ($code)"
      rm -f "$body"
      return
    fi
    [[ "$attempt" == 6 ]] || sleep 10
  done
  echo "  [FAIL] $description: got $code, expected $status${text:+ with \"$text\"}" >&2
  head -c 300 "$body" >&2 || true
  echo >&2
  rm -f "$body"
  failed=1
}

echo "Smoke check of $BASE_URL"
check "gateway /healthz" 200 "ok" "$BASE_URL/healthz"
check "web app /" 200 "<app-root" "$BASE_URL/"
check "API through the gateway: unknown client is refused" 401 "invalid_client" \
  -X POST -H 'Content-Type: application/json' -d '{"clientId":"smoke-check","clientSecret":"none"}' \
  "$BASE_URL/api/auth/token"

if [[ -n "${SMOKE_CLIENT_ID:-}" && -n "${SMOKE_CLIENT_SECRET:-}" ]]; then
  token="$(curl -sS --max-time 10 -X POST -H 'Content-Type: application/json' \
      -d "{\"clientId\":\"$SMOKE_CLIENT_ID\",\"clientSecret\":\"$SMOKE_CLIENT_SECRET\"}" \
      "$BASE_URL/api/auth/token" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')"
  if [[ -z "$token" ]]; then
    echo "  [FAIL] sign-in as $SMOKE_CLIENT_ID" >&2
    failed=1
  else
    check "signed in, GET /api/categories (API and database)" 200 "" \
      -H "Authorization: Bearer $token" "$BASE_URL/api/categories"
  fi
else
  echo "  (no SMOKE_CLIENT_ID / SMOKE_CLIENT_SECRET: skipped the signed-in read)"
fi

if [[ "$failed" != 0 ]]; then
  echo "Smoke check FAILED" >&2
  exit 1
fi
echo "Smoke check passed."
