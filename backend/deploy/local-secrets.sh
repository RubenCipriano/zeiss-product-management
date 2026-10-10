#!/usr/bin/env bash
# Creates the API's local development secrets on this machine, once. Every clone generates its own, so no private
# key, secret hash or password file is ever committed. deploy/deploy.sh runs this before it renders the local overlay.
#
#   deploy/local-secrets.sh             # creates what is missing, keeps what exists
#   FORCE=1 deploy/local-secrets.sh     # new key, hashes and password file (signs everyone out; then deploy/deploy.sh)
#
# Writes (gitignored, readable only by you), into deploy/k8s/overlays/local/generated/ → the Secret api-secrets:
#   signing-key.pem   a new ES256 private key (PKCS#8 PEM) that signs access tokens        → key signing-key
#   consumers.json    the demo consumers viewer/viewer, clerk/clerk, manager/manager       → key consumers.json
#                     (client ids, PBKDF2 hashes of their secrets, roles)
#   passwords.env     the passwords of the local database user pm_app and of Redis         → keys database-password,
#                     the infra repository's local defaults, or PM_APP_PASSWORD and          redis-password
#                     REDIS_PASSWORD when you started the infra with other values
# and the API's .NET user-secrets: the same key and consumers, for `dotnet run` (Development).
#
# Needs openssl and python3. The user-secrets step needs the .NET SDK and is skipped without it (run the script again
# once it is installed). Staging and production never use these: their secrets come from CI/CD variables
# (deploy/ci/create-secrets.sh).
set -euo pipefail

BACKEND="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$BACKEND/deploy/k8s/overlays/local/generated"
API_PROJECT="$BACKEND/ProductManagement.Api"

generated=false
if [[ "${FORCE:-0}" == 1 || ! -f "$OUT/signing-key.pem" || ! -f "$OUT/consumers.json" ]]; then
  echo "==> Creating the local secrets (deploy/k8s/overlays/local/generated): a new signing key, the demo consumers"
  mkdir -p "$OUT"
  (
    umask 077
    # named_curve: without it LibreSSL (macOS) writes the curve's explicit parameters, which the API rejects.
    openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -pkeyopt ec_param_enc:named_curve \
      -out "$OUT/signing-key.pem"
    # The same format as the API's SecretHasher: pbkdf2-sha256$<iterations>$<salt>$<hash>, PBKDF2-HMAC-SHA256 over the
    # UTF-8 secret, 16-byte random salt, 100,000 iterations, 32-byte hash, standard Base64.
    python3 - > "$OUT/consumers.json" <<'PY'
import base64, hashlib, json, os

def hash_secret(secret):
    salt = os.urandom(16)
    digest = hashlib.pbkdf2_hmac("sha256", secret.encode("utf-8"), salt, 100_000, 32)
    return "$".join(["pbkdf2-sha256", "100000", base64.b64encode(salt).decode(), base64.b64encode(digest).decode()])

# Local demo accounts: the client id is also the secret.
consumers = [("viewer", "viewer"), ("clerk", "stock-clerk"), ("manager", "manager")]
print(json.dumps({"Authentication": {"Consumers": [
    {"ClientId": client_id, "SecretHash": hash_secret(client_id), "Roles": [role]} for client_id, role in consumers
]}}, indent=2))
PY
  )
  generated=true
fi

# The same defaults as the infra repository's database.yaml and redis.yaml (local development values only).
if [[ "${FORCE:-0}" == 1 || ! -f "$OUT/passwords.env" ]]; then
  echo "==> Writing the local database and Redis passwords (deploy/k8s/overlays/local/generated/passwords.env)"
  mkdir -p "$OUT"
  (
    umask 077
    {
      echo "database-password=${PM_APP_PASSWORD:-local-app-password}"
      echo "redis-password=${REDIS_PASSWORD:-local-redis-password}"
    } > "$OUT/passwords.env"
  )
fi

# The same key and consumers for `dotnet run`: .NET loads user-secrets in Development (UserSecretsId in the project).
if ! command -v dotnet >/dev/null; then
  echo "    no .NET SDK: skipped the user-secrets for dotnet run"
elif $generated \
  || ! dotnet user-secrets list --project "$API_PROJECT" 2>/dev/null | grep '^Authentication:SigningKey = ' >/dev/null; then
  echo "==> Storing them in the API's .NET user-secrets (dotnet run)"
  python3 - "$OUT/signing-key.pem" "$OUT/consumers.json" <<'PY' \
    | dotnet user-secrets set --project "$API_PROJECT" >/dev/null \
    || echo "    could not write the user-secrets (dotnet user-secrets); dotnet run will not start without them" >&2
import json, sys

with open(sys.argv[2]) as consumers, open(sys.argv[1]) as key:
    settings = json.load(consumers)
    settings["Authentication"]["SigningKey"] = key.read()
print(json.dumps(settings))
PY
fi
