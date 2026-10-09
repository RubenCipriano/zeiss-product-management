# Web image: the compiled Angular app served by unprivileged nginx.
# Build from this folder:  docker build -t product-management/web:local .

FROM node:24-alpine AS build
WORKDIR /app

# Dependencies in their own layer, cached until the lockfile changes.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# Unprivileged nginx: listens on 8080 and runs as a non-root user.
FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/product-management-web/browser /usr/share/nginx/html
EXPOSE 8080
