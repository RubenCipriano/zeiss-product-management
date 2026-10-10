#!/usr/bin/env bash
# Gives a service its own database on the shared PostgreSQL (one server for the platform, one database per service),
# with two users, and puts their passwords in a Secret in the service's namespace.
#
#   ./create-database.sh <service-namespace> <database> [app-user] [migrator-user] [--rotate]
#   ./create-database.sh product-management product_management pm_app pm_migrator
#
#   app-user        what the service runs as: reads and writes rows, nothing else; its sessions end a statement
#                   after 30 s and an idle open transaction after 60 s. Default: <database>_app
#   migrator-user   owns the schema: creates and changes tables (the service's migrations). Default: <database>_migrator
#   which rows and tables the app user may use is granted by the service's own migrations (they create the tables);
#   for product-management: the migration GrantAppPermissions.
#
# Secret <service-namespace>/database-credentials:
#   host, port, database, app-user, app-password, migrator-user, migrator-password
#
# Idempotent: run it again at any time (e.g. after restoring a dump as the administrator: objects owned by someone
# else are handed to the migrator user). It keeps the existing passwords unless --rotate is given; after a rotation,
# restart the service's pods so they read the new Secret. Runs psql inside the PostgreSQL pod (kubectl exec), so
# nothing but kubectl is needed here. Passwords travel on stdin, never on a command line.
set -euo pipefail

INFRA_NAMESPACE=infra
SECRET="database-credentials"
HOST=postgres.infra.svc.cluster.local
PORT=5432

usage() {
  echo "usage: $0 <service-namespace> <database> [app-user] [migrator-user] [--rotate]" >&2
  exit 1
}

rotate=false
args=()
for arg in "$@"; do
  case "$arg" in
    --rotate) rotate=true ;;
    -*) usage ;;
    *) args+=("$arg") ;;
  esac
done
[[ ${#args[@]} -ge 2 && ${#args[@]} -le 4 ]] || usage
namespace="${args[0]}"
database="${args[1]}"
app_user="${args[2]:-${database}_app}"
migrator_user="${args[3]:-${database}_migrator}"

# Names go into SQL as identifiers: plain lower-case ones only.
for name in "$database" "$app_user" "$migrator_user"; do
  if [[ ! "$name" =~ ^[a-z_][a-z0-9_]{0,62}$ ]]; then
    echo "Invalid name: $name (lower-case letters, digits and _)" >&2
    exit 1
  fi
done

random_password() {
  od -An -tx1 -N24 /dev/urandom | tr -d ' \n'
}

secret_value() {
  kubectl -n "$namespace" get secret "$SECRET" -o go-template="{{ index .data \"$1\" | base64decode }}"
}

# The passwords: the existing ones unless --rotate (or there are none yet).
if ! $rotate && kubectl -n "$namespace" get secret "$SECRET" >/dev/null 2>&1; then
  echo "==> Keeping the passwords in $namespace/$SECRET (--rotate for new ones)"
  app_password="$(secret_value app-password)"
  migrator_password="$(secret_value migrator-password)"
else
  echo "==> New passwords for $app_user and $migrator_user"
  app_password="$(random_password)"
  migrator_password="$(random_password)"
fi

echo "==> Database $database, users $app_user and $migrator_user (in $INFRA_NAMESPACE/postgres)"
kubectl -n "$INFRA_NAMESPACE" exec -i statefulset/postgres -- \
  psql -X -q -U postgres -d postgres -v ON_ERROR_STOP=1 \
  -v db="$database" -v app="$app_user" -v migrator="$migrator_user" <<SQL
\set app_password '$app_password'
\set migrator_password '$migrator_password'
SET client_min_messages = warning;

-- The users (roles are server-wide); their passwords follow the Secret on every run.
SELECT format('CREATE ROLE %I LOGIN', :'migrator') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'migrator') \gexec
SELECT format('CREATE ROLE %I LOGIN', :'app') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'app') \gexec
ALTER ROLE :"migrator" PASSWORD :'migrator_password';
ALTER ROLE :"app" PASSWORD :'app_password';

-- Guard rails for the service's sessions: a runaway query or a transaction left open by a bug is ended by the
-- server instead of holding locks and connections forever.
ALTER ROLE :"app" SET statement_timeout = '30s';
ALTER ROLE :"app" SET idle_in_transaction_session_timeout = '60s';

-- The database: nobody connects unless granted. The migrator may create trusted extensions (e.g. pg_trgm).
SELECT format('CREATE DATABASE %I', :'db') WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'db') \gexec
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO :"migrator", :"app";
GRANT CREATE ON DATABASE :"db" TO :"migrator";

\connect :"db"
SET client_min_messages = warning;

-- Query statistics (shared_preload_libraries in postgres.yaml); creating it needs the administrator.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- The schema belongs to the migrator; the app user may only use it.
ALTER SCHEMA public OWNER TO :"migrator";
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO :"app";

-- Tables and sequences created by someone else (e.g. a dump restored as the administrator) move to the migrator.
-- Tables first: their identity/serial sequences follow them; then any standalone sequence.
SELECT format('ALTER TABLE %s OWNER TO %I', c.oid::regclass, :'migrator')
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND pg_get_userbyid(c.relowner) <> :'migrator' \gexec
SELECT format('ALTER SEQUENCE %s OWNER TO %I', c.oid::regclass, :'migrator')
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'S' AND pg_get_userbyid(c.relowner) <> :'migrator' \gexec
SQL

echo "==> Secret $namespace/$SECRET"
kubectl create namespace "$namespace" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl -n "$namespace" create secret generic "$SECRET" \
    --from-literal=host="$HOST" --from-literal=port="$PORT" --from-literal=database="$database" \
    --from-literal=app-user="$app_user" --from-literal=app-password="$app_password" \
    --from-literal=migrator-user="$migrator_user" --from-literal=migrator-password="$migrator_password" \
    --dry-run=client -o yaml \
  | kubectl label --local -f - -o yaml app.kubernetes.io/managed-by=create-database.sh \
  | kubectl apply -f -

if $rotate; then
  echo "New passwords: restart the service's pods so they read them, e.g."
  echo "  kubectl -n $namespace rollout restart deployment"
fi
echo "Connection: Host=$HOST;Port=$PORT;Database=$database;Username=$app_user (password: $SECRET/app-password)"
