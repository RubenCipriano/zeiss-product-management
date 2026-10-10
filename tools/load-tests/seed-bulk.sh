#!/usr/bin/env bash
# Loads the bulk test data (data/bulk-seed.sql) into PostgreSQL, optionally grows the stock ledger
# (data/bulk-ledger.sql), then invalidates the API cache and checks the data invariants.
#
#   tools/load-tests/seed-bulk.sh                              # 850,000 products, ~5 million stock movements (~3-6 minutes)
#   PRODUCTS=100000 tools/load-tests/seed-bulk.sh
#   PRODUCTS=0 LEDGER_ROWS=20000000 tools/load-tests/seed-bulk.sh   # only grow the ledger to 20 million rows (~20 minutes)
#
# Adds to the existing data; it does not delete anything. To start again from the migration's sample data, recreate
# the database and run the migrations again.
#
# The servers: by default the shared PostgreSQL and Redis in the cluster (namespace infra), reached with
# `kubectl exec` (psql as the administrator; redis-cli finds Redis' password in its pod). To target other servers, set
#   PSQL       the psql command, e.g. PSQL='psql -h db.internal -U postgres -d product_management'
#              (password from PGPASSWORD or ~/.pgpass);
#   REDIS_CLI  the redis-cli command, e.g. REDIS_CLI='redis-cli -h cache.internal -a secret --no-auth-warning';
# or only PG_USER / PG_DATABASE for another user or database.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PRODUCTS="${PRODUCTS:-850000}"
LEDGER_ROWS="${LEDGER_ROWS:-}"
REDIS_KEY_PREFIX="${REDIS_KEY_PREFIX:-product-management}"

if [[ -n "${PSQL:-}" ]]; then read -r -a psql_cmd <<< "$PSQL"
else psql_cmd=(kubectl -n infra exec -i statefulset/postgres -- psql -U "${PG_USER:-postgres}" -d "${PG_DATABASE:-product_management}"); fi
if [[ -n "${REDIS_CLI:-}" ]]; then read -r -a redis_cmd <<< "$REDIS_CLI"
else redis_cmd=(kubectl -n infra exec -i deployment/redis -- redis-cli); fi

psql() { "${psql_cmd[@]}" -X "$@"; }
redis() { "${redis_cmd[@]}" "$@"; }

if [[ "$PRODUCTS" -gt 0 ]]; then
  echo "==> Seeding $PRODUCTS products and their stock history"
  psql -v products="$PRODUCTS" -f - < "$HERE/data/bulk-seed.sql"
fi

if [[ -n "$LEDGER_ROWS" ]]; then
  echo "==> Growing the stock ledger to $LEDGER_ROWS movements"
  psql -v ledger_rows="$LEDGER_ROWS" -v batch_rows="${LEDGER_BATCH_ROWS:-200000}" -f - < "$HERE/data/bulk-ledger.sql"
fi

# Cached entries still describe the old catalogue: move the generations on, as the API does after creating
# products (data: stock-level lists; listing: the full list and searches, their counts and page ids, and through
# it every product's details and history).
echo "==> Invalidating the API cache"
redis INCR "$REDIS_KEY_PREFIX:catalog:generation" >/dev/null
redis INCR "$REDIS_KEY_PREFIX:catalog:listing-generation" >/dev/null

echo "==> Checking the data invariants"
"$HERE/verify-data.sh"
