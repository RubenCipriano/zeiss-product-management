#!/usr/bin/env bash
# Checks the data invariants (data/verify.sql) in PostgreSQL: unique ids, stock equal to the ledger, consistent
# running totals. Every count must be 0; the script fails otherwise.
#
# By default it runs psql in the shared PostgreSQL in the cluster (kubectl -n infra exec statefulset/postgres). Set
# PSQL to the psql command of another server (e.g. PSQL='psql -h db.internal -U postgres -d product_management'), or
# PG_USER / PG_DATABASE for another user or database.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

if [[ -n "${PSQL:-}" ]]; then read -r -a psql_cmd <<< "$PSQL"
else psql_cmd=(kubectl -n infra exec -i statefulset/postgres -- psql -U "${PG_USER:-postgres}" -d "${PG_DATABASE:-product_management}"); fi

result="$("${psql_cmd[@]}" -X -v ON_ERROR_STOP=1 -f - < "$HERE/data/verify.sql")"
echo "$result"

# The first result row holds the six counts.
counts="$(echo "$result" | awk -F'|' 'f==0 && /^-+\+/ {f=1; next} f==1 {print; exit}')"
if echo "$counts" | tr '|' '\n' | grep -qvE '^\s*0\s*$'; then
  echo "FAILED: an invariant is broken" >&2
  exit 1
fi
echo "OK: all invariants hold"
