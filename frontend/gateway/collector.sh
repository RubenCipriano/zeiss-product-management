#!/bin/sh
# Decides what the gateway does with the browser's error reports (POST /collect), from COLLECTOR_UPSTREAM:
#   host:port   forward them to the log collector (Grafana Alloy's Faro receiver in the cluster)
#   ""          there is no log collector: answer 204 No Content and drop them, so the browser sees no error
# The nginx image's entrypoint runs this before nginx starts (it is /docker-entrypoint.d/25-collector.sh, after the
# templates are rendered). default.conf.template includes the two files written here.
set -eu

upstream_file=/etc/nginx/collector-upstream.conf
location_file=/etc/nginx/collect-location.conf

if [ -n "${COLLECTOR_UPSTREAM:-}" ]; then
  # `resolve`: the name is looked up at run time, so the gateway also starts when the collector is not there yet
  # (/collect then answers 502 and the browser drops the report).
  cat > "$upstream_file" <<CONF
upstream collector {
  zone collector 64k;
  server ${COLLECTOR_UPSTREAM} resolve;
  keepalive 4;
}
CONF
  cat > "$location_file" <<'CONF'
proxy_read_timeout 5s;
proxy_pass http://collector;
CONF
  echo "$0: /collect is forwarded to ${COLLECTOR_UPSTREAM}"
else
  echo "# COLLECTOR_UPSTREAM is empty: no log collector, no upstream." > "$upstream_file"
  echo "return 204;" > "$location_file"
  echo "$0: COLLECTOR_UPSTREAM is empty: /collect answers 204 and the reports are dropped"
fi
