# Gateway image: the nginx edge in front of the whole application.
# Build from this folder:  docker build -t product-management/gateway:local .
#
# The official image renders /etc/nginx/templates/*.template with environment variables at startup, so rate
# limits and upstream addresses are configured per environment without rebuilding the image.
FROM nginxinc/nginx-unprivileged:1.29-alpine

COPY nginx.conf /etc/nginx/nginx.conf
COPY default.conf.template /etc/nginx/templates/default.conf.template
COPY security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY jwt.js trace.js /etc/nginx/njs/
# Run by the image's entrypoint at startup: forward /collect to COLLECTOR_UPSTREAM, or answer 204 when it is empty.
COPY --chmod=755 collector.sh /docker-entrypoint.d/25-collector.sh

# Defaults; override them in the Kubernetes Deployment (the deploy repository). COLLECTOR_UPSTREAM="" = no log collector.
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    GATEWAY_RATE_LIMIT_RPS=200 \
    GATEWAY_RATE_LIMIT_BURST=400 \
    GATEWAY_TRUSTED_PROXIES=127.0.0.1/32 \
    WEB_UPSTREAM=web.product-management.svc.cluster.local:8080 \
    API_UPSTREAM=api.product-management.svc.cluster.local:8080 \
    COLLECTOR_UPSTREAM=alloy-faro.infra.svc.cluster.local:12347 \
    JWKS_URL=http://api.product-management.svc.cluster.local:8080/.well-known/jwks.json \
    JWT_ISSUER=product-management-api \
    JWT_AUDIENCE=product-management-api

EXPOSE 8080
