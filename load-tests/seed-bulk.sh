#!/usr/bin/env bash
# Loads the bulk test data (data/bulk-seed.sql) into the cluster's PostgreSQL, then invalidates the API cache.
#
#   load-tests/seed-bulk.sh                    # 850,000 products, ~5 million stock movements (~3-6 minutes)
#   PRODUCTS=100000 load-tests/seed-bulk.sh
#
# Adds to the existing data; it does not delete anything. To start again from the migration's sample data,
# delete the PostgreSQL volume (kubectl -n product-management delete pvc data-postgres-0 and the pod) and redeploy.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
NAMESPACE=product-management
PRODUCTS="${PRODUCTS:-850000}"

psql() {
  kubectl -n "$NAMESPACE" exec -i postgres-0 -- sh -c 'psql -X -U "$POSTGRES_USER" -d "$POSTGRES_DB" "$@"' psql "$@"
}

echo "==> Seeding $PRODUCTS products and their stock history"
psql -v products="$PRODUCTS" -f - < "$HERE/data/bulk-seed.sql"

# Cached entries still describe the old catalogue: move the generations on, as the API does after creating
# products (data: stock-level lists; listing: the full list and searches, their counts and page ids).
echo "==> Invalidating the API cache"
kubectl -n "$NAMESPACE" exec deploy/redis -- redis-cli INCR product-management:catalog:generation >/dev/null
kubectl -n "$NAMESPACE" exec deploy/redis -- redis-cli INCR product-management:catalog:listing-generation >/dev/null

echo "==> Checking the data invariants"
"$HERE/verify-data.sh"
