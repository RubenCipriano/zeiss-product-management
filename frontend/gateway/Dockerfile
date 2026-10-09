# Gateway image: the nginx edge in front of the whole application.
# Build from this folder:  docker build -t product-management/gateway:local .
#
# The official image renders /etc/nginx/templates/*.template with environment variables at startup, so rate
# limits and upstream addresses are configured per environment without rebuilding the image.
FROM nginxinc/nginx-unprivileged:1.29-alpine

COPY nginx.conf /etc/nginx/nginx.conf
COPY default.conf.template /etc/nginx/templates/default.conf.template
COPY security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY jwt.js /etc/nginx/njs/jwt.js

# Defaults; override them in the Kubernetes Deployment.
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    GATEWAY_RATE_LIMIT_RPS=200 \
    GATEWAY_RATE_LIMIT_BURST=400 \
    WEB_UPSTREAM=web.product-management.svc.cluster.local:8080 \
    API_UPSTREAM=api-headless.product-management.svc.cluster.local:8080 \
    JWKS_URL=http://api.product-management.svc.cluster.local:8080/.well-known/jwks.json \
    JWT_ISSUER=product-management-api \
    JWT_AUDIENCE=product-management-api

EXPOSE 8080
