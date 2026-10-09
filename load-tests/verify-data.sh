#!/usr/bin/env bash
# Checks the data invariants (data/verify.sql) in the cluster's PostgreSQL: unique ids, stock equal to the ledger,
# consistent running totals. Every count must be 0; the script fails otherwise.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

result="$(kubectl -n product-management exec -i postgres-0 -- \
  sh -c 'psql -X -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -f -' < "$HERE/data/verify.sql")"
echo "$result"

# The first result row holds the six counts.
counts="$(echo "$result" | awk -F'|' 'f==0 && /^-+\+/ {f=1; next} f==1 {print; exit}')"
if echo "$counts" | tr '|' '\n' | grep -qvE '^\s*0\s*$'; then
  echo "FAILED: an invariant is broken" >&2
  exit 1
fi
echo "OK: all invariants hold"
