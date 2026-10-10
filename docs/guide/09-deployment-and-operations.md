# 9. Deployment and operations

**After this chapter you will know** what runs where (everything in Kubernetes: the application in its namespace,
the shared PostgreSQL, Redis and logs in the namespace `infra`), how the deployment is split into three parts that
each deploy on their own, how each part's manifests are split into a base and one overlay per environment, where the
configuration and the passwords come from, what each script does step by step, how and why migrations run before the
rollout, inside the cluster and as which database user, what the CI/CD pipelines do, and the commands for day-to-day
operation: rollouts, rollbacks, secret rotation, scaling, backups and restores, troubleshooting, resetting data and
running load tests.

![What runs where: everything inside the Kubernetes cluster, the namespace product-management with the gateway, web and API, and the namespace infra with PostgreSQL, Redis, Loki, Grafana and Alloy](../images/kubernetes-map.svg)

---

## The layout: three parts

The system is in three self-contained parts, each its own GitLab repository with its own pipeline: backend,
frontend and deploy (the folder `infra/`) ([ADR 0021](../adr/0021-deployment-layout.md)). Each one has its code (or configuration), its deploy script
and its manifests together:

```text
infra/                              the shared infrastructure, namespace `infra`, for every service on the cluster
├── infra.sh                        infra.sh up|down|status [postgres] [redis] [logs] [--delete-data]
├── postgres.yaml                   PostgreSQL 18: StatefulSet, Service, settings (postgresql.conf inline), nightly pg_dump
├── redis.yaml                      Redis 8: Deployment, Service, settings (redis.conf inline)
├── logs.yaml                       Loki, Grafana (data source, alert rules), Alloy (every pod's logs, the browser's errors)
├── dashboards/                     the two Grafana dashboards (JSON), loaded by infra.sh into a ConfigMap
├── create-database.sh              one database per service, with its two users and the Secret database-credentials
├── add-node.sh                     joins a machine to a k3s cluster as a database, cache or apps node
└── .gitlab-ci.yml

backend/                            the API
├── deploy/
│   ├── deploy.sh                   [ENVIRONMENT=local|staging|production] deploy/deploy.sh
│   ├── local-secrets.sh            the local signing key and demo consumers, generated once per machine
│   ├── k8s/base/                   namespace, deployment.yaml (configuration as env keys), service.yaml (`api`,
│   │                               headless), hpa.yaml, pdb.yaml, network-policy.yaml
│   ├── k8s/overlays/<env>/         kustomization.yaml + api.yaml (connection strings, per-environment settings);
│   │                               local also generates the Secret api-secrets from generated/ (gitignored)
│   └── ci/                         validate-manifests.sh, create-secrets.sh, deploy-environment.sh
├── load-tests/, scripts/check-requirements.py
└── .gitlab-ci.yml

frontend/                           the web app and the gateway
├── gateway/                        the gateway image (nginx config template, collector.sh, njs)
├── deploy/
│   ├── deploy.sh                   [ENVIRONMENT=...] [LOGS=off] deploy/deploy.sh
│   ├── k8s/base/                   namespace, web.yaml, gateway.yaml
│   ├── k8s/overlays/<env>/         kustomization.yaml + gateway.yaml (API and collector addresses);
│   │                               staging and production add ingress.yaml
│   └── ci/validate-manifests.sh
└── .gitlab-ci.yml
```

Deploy them in this order; each step only needs what the one before it started:

```bash
infra/infra.sh up                 # PostgreSQL, Redis, Loki + Grafana + Alloy in the namespace infra
infra/create-database.sh product-management product_management pm_app pm_migrator   # once per cluster
backend/deploy/deploy.sh          # secrets (first run), API image, migrations, rollout
frontend/deploy/deploy.sh         # web + gateway images, rollout → http://localhost:8080
```

**What connects the parts.** Only addresses and Secrets, written in each part's own files:

| From | To | Where it is set |
| --- | --- | --- |
| backend (the API) | PostgreSQL, Redis | `ConnectionStrings__ProductManagement` (`postgres.infra.svc.cluster.local:5432`, user `pm_app`) and `ConnectionStrings__Redis` (`redis.infra.svc.cluster.local:6379`) in `backend/deploy/k8s/overlays/<env>/api.yaml` |
| backend (the migration pod) | PostgreSQL, Redis | the database's host, port, name and the migrator user from the Secret `database-credentials`; Redis from `MIGRATION_REDIS` (default `redis.infra.svc.cluster.local:6379`) |
| backend | the database and Redis passwords | the Secrets `database-credentials` (made by `infra/create-database.sh`) and `redis-credentials` (copied from `infra/redis-auth` by `backend/deploy/deploy.sh`) |
| frontend (the gateway) | the API | `API_UPSTREAM`, `JWKS_URL` in `frontend/deploy/k8s/overlays/<env>/gateway.yaml`: the Service `api.product-management.svc.cluster.local:8080` |
| frontend (the gateway) | the log collector (optional) | `COLLECTOR_UPSTREAM` in the same file: `alloy-faro.infra.svc.cluster.local:12347`, or `""` with `LOGS=off` |
| infra (Alloy) | Loki | the Loki in `infra` by default; `LOKI_URL` for any other Loki |

Both the backend and the frontend apply the namespace `product-management` (the same object): whichever deploys
first creates it. `create-database.sh` creates it too if it is missing, so it can run before the first deployment.

**Base and overlays.** In the backend and the frontend, `deploy/k8s/base` holds everything that runs in the cluster,
the same in every environment; `deploy/k8s/overlays/<env>` holds only what differs, as one strategic-merge patch per
Deployment (`api.yaml`, `gateway.yaml`) plus, in staging and production, the frontend's Ingress. `kubectl kustomize`
is built into `kubectl`, so nothing else is installed. Each part's `deploy/ci/validate-manifests.sh` checks that every
overlay renders, is valid against the Kubernetes schemas and sets what it must: the backend's both connection strings
(and no Secret outside `local`), the frontend's `API_UPSTREAM`, `JWKS_URL` and `COLLECTOR_UPSTREAM` (and no Secret
at all). The infra's manifests are plain files, the same in every environment; `infra/.gitlab-ci.yml` checks them
with `kubeconform`.

**Configuration is plain environment variables in the Deployment.** The API's settings are `env` entries in
`backend/deploy/k8s/base/deployment.yaml` (ASP.NET Core reads `Section__Key` as `Section:Key`): token lifetime, rate
limits, sign-in throttle, idempotency lifetimes, `Database__ApplyMigrationsOnStartup=false`, `OpenApi__Enabled`. An
overlay's `api.yaml` sets only what differs, and always the two connection strings. Because the values are in the pod
template, changing one rolls the pods like a code change, and `kubectl rollout undo` restores the old configuration
with the old code. The gateway works the same way (`GATEWAY_RATE_LIMIT_*`, `GATEWAY_TRUSTED_PROXIES`, `JWT_*` in the
base; the addresses in the overlays).

**Three Secrets, no password in a connection string or a pipeline variable.** The API reads:

| Secret | Key | Becomes in the API | Made by |
| --- | --- | --- | --- |
| `database-credentials` | `app-password` | `PGPASSWORD`: Npgsql uses it when the connection string has no password (the readiness check's own connection too) | `infra/create-database.sh`, once per environment: a random password, kept on every re-run |
| `redis-credentials` | `password` | `Redis__Password`: the API sets it on the Redis connection, over any password in the connection string | `backend/deploy/deploy.sh`, on every deploy: a copy of `infra/redis-auth`, because a pod can only read Secrets of its own namespace |
| `api-secrets` | `signing-key` | `Authentication__SigningKey`: the ES256 private key that signs tokens | locally `generated/signing-key.pem`; staging and production the GitLab variable `API_SIGNING_KEY` (File) |
| `api-secrets` | `consumers.json` | the file `/app/config/consumers.json`: client ids, PBKDF2 hashes, roles | locally `generated/consumers.json`; staging and production `API_CONSUMERS` (File) |

The database and Redis passwords are random, created in the cluster (`infra.sh` makes `postgres-admin` and
`redis-auth`, `create-database.sh` the users' passwords) and never leave it: no file, no pipeline variable holds
them. `api-secrets` holds only the API's own keys. Locally `backend/deploy/local-secrets.sh` writes the two files into
`backend/deploy/k8s/overlays/local/generated/` (gitignored, mode 600) once per machine, and the overlay's
`secretGenerator` makes `api-secrets-<hash>` from them: a changed file gives the Secret a new name, which changes the
Deployment and rolls the pods. In staging and production the overlays contain no Secret; the deploy job creates
`api-secrets` from CI/CD variables (`backend/deploy/ci/create-secrets.sh`).

**The shared infrastructure runs in the cluster.** PostgreSQL, Redis and the logs (Loki, Grafana, Alloy) run in
Kubernetes like the application, in one namespace, `infra`, shared by every service on the cluster: one PostgreSQL
with one database per service (`infra/create-database.sh`), one Redis (services separate their keys by prefix), and
one log pipeline that collects every pod's log without the service doing anything. The same files run on Docker
Desktop, on a single k3s machine and on a production cluster, where `infra/add-node.sh` gives PostgreSQL and Redis
nodes of their own ([chapter 12](12-from-zero.md)). Kubernetes restarts, schedules and passes the passwords for them
as for any workload; their data is on PersistentVolumeClaims that `infra.sh down` never deletes
([ADR 0021, update](../adr/0021-deployment-layout.md)). A managed database or cache instead is a host name in the
backend's overlay (the comments in `api.yaml` say how, with TLS).

**Logs are optional.** The `logs` component (Loki, Grafana, Alloy) can be left out:
`infra/infra.sh up postgres redis` and `LOGS=off frontend/deploy/deploy.sh`. The application works the same; the
pods log to stdout (`kubectl logs`) and the gateway answers the browser's error reports with 204. With
`LOKI_URL=… infra/infra.sh up logs` the logs go to another Loki instead (no Loki or Grafana in the cluster).

## What runs where

**Namespace `infra`** (the infra repository, `infra/infra.sh up`):

| Object | Kind | Component | What it does |
| --- | --- | --- | --- |
| `postgres` | StatefulSet (1 pod, `postgres-0`) + Service | postgres ([`postgres.yaml`](../../infra/postgres.yaml)) | PostgreSQL 18 at `postgres.infra.svc.cluster.local:5432`, data on the PVC `data-postgres-0` (20 GiB), 4 CPUs and 3 GiB, 1 GiB `/dev/shm`, `max_connections=200`, `shared_buffers=768MB`, `pg_stat_statements` preloaded, slow statements (> 250 ms), lock waits, checkpoints and autovacuum runs logged; sized from the measurements in [ADR 0022](../adr/0022-database-at-scale.md). The settings are the ConfigMap `postgres-config`; a `preStop` hook makes a fast shutdown |
| `postgres-backup` | CronJob + PVC `postgres-backups` (20 GiB) | postgres | every night at 02:30: `pg_dumpall --globals-only` and `pg_dump -Fc` of every database into `/backups/<date-time>/`, 7 days kept (below, "Backups and restores") |
| `postgres-admin` | Secret | postgres | the superuser `postgres`'s random password, for `create-database.sh`, the backups and people; never for a service |
| `redis` | Deployment (1 pod, `Recreate`) + Service | redis ([`redis.yaml`](../../infra/redis.yaml)) | Redis 8 at `redis.infra.svc.cluster.local:6379`, password from the Secret `redis-auth` (`--requirepass`), memory only (no volume), `maxmemory 256mb`, `volatile-lru` (only keys with an expiry can be evicted), `FLUSHALL`/`FLUSHDB`/`DEBUG` disabled |
| `loki` | StatefulSet + Service | logs ([`logs.yaml`](../../infra/logs.yaml)) | Loki 3.5.5 at `loki:3100`, 14 days' retention, data on the PVC `data-loki-0` (10 GiB) |
| `grafana` | Deployment + Service | logs | Grafana 12.2.0 at `grafana:3000`, no volume: the Loki data source, the two dashboards ([`dashboards/`](../../infra/dashboards)) and the 12 alert rules are provisioned from ConfigMaps ([chapter 8](08-observability.md)); anonymous administrator, reached only through a port-forward |
| `alloy` | Deployment + ServiceAccount, ClusterRole `infra-alloy` (read pods and their logs) | logs | Grafana Alloy: reads the log of every pod in every namespace through the Kubernetes API (PostgreSQL, Redis, Loki and Grafana included) and pushes it to Loki as `{namespace, app}`; receives the browser's error reports at the Service `alloy-faro:12347`, which a NetworkPolicy (`alloy-ingress`) reserves for the gateway pods of `product-management` |

The shared infrastructure's own logs are `{namespace="infra", app="postgres"}` (or `redis`, `loki`, `grafana`,
`postgres-backup`).

**Database users** (made by [`create-database.sh`](../../infra/create-database.sh), one pair per service):
`pm_migrator` owns the schema `public` and is used only by the migration pod; `pm_app` is the API's user, with
`CONNECT` and `USAGE` only. What `pm_app` may do on the tables is granted by the service's own migration
`GrantAppPermissions`, which runs as `pm_migrator`: read and write rows and draw ids from the sequences, on today's
tables and every later one (`ALTER DEFAULT PRIVILEGES`), nothing else (`CREATE TABLE`: "permission denied for schema
public"; `ALTER`, `DROP`: "must be owner"). A statement of `pm_app` is cancelled after 30 s and a transaction left
idle after 60 s. The superuser `postgres` is used by the infra scripts and by people, never by an application.

**Namespace `product-management`:**

| Object | Kind | Part | What it does |
| --- | --- | --- | --- |
| `database-credentials` | Secret | infra (`create-database.sh`) | `host`, `port`, `database`, `app-user`, `app-password`, `migrator-user`, `migrator-password`. The API reads only `app-password`; the migration pod the migrator's keys. |
| `redis-credentials` | Secret | backend (`deploy.sh`) | The shared Redis' password, copied from `infra/redis-auth` on every deploy. |
| `api-secrets` (locally `api-secrets-<hash>`) | Secret | backend (local overlay / pipeline) | The signing key and the consumers (above). |
| `api` | Deployment (no fixed count) | backend | The API pods, with their configuration as env keys. Rolling update with `maxUnavailable: 0`, a 5 s `preStop` pause, startup, readiness and liveness probes, 5 old ReplicaSets kept. Pool of 10 database connections per pod, plus one for the readiness check. |
| `api` | Service (headless, `clusterIP: None`) | backend | **The API's only Service.** `api.product-management.svc.cluster.local` lists every ready API pod; the gateway balances over them per request and downloads the public signing key from it ([chapter 2](02-request-journey.md#why-the-apis-service-is-headless)). |
| `api` | HorizontalPodAutoscaler | backend | 2–8 pods at 60 % CPU ([chapter 6](06-scaling-and-capacity.md)). |
| `api` | PodDisruptionBudget | backend | At least one pod stays up during node maintenance. |
| `api-ingress` | NetworkPolicy | backend | Only the gateway may connect. |
| `migrate-<tag>` | Pod (only while it runs) | backend (`deploy.sh`) | The migrations, once per deployment (below); deleted when it ends. |
| `web` | Deployment (2 pods) + Service + PDB | frontend | nginx serving the Angular build. |
| `web-ingress` | NetworkPolicy | frontend | Only the gateway may connect. |
| `gateway` | Deployment (no fixed count) + PDB + HPA | frontend | The nginx gateway, 2–6 pods at 70 % CPU. |
| `gateway` | Service | frontend | Port 8080. Locally type `LoadBalancer` (`localhost:8080` on Docker Desktop). |
| `gateway` | Ingress | frontend (staging, production) | HTTPS for `staging.products.example.com` / `products.example.com`, certificate from cert-manager (Let's Encrypt). |

**`kube-system`:** metrics-server, which feeds the two HPAs and `kubectl top`. k3s and most managed clusters ship
it; where it is missing (Docker Desktop), `infra/infra.sh up` installs it (v0.8.0).

The application namespace holds no data: everything that must survive is in `infra`, on the PVCs `data-postgres-0`,
`postgres-backups` and `data-loki-0`, and in the Secrets `postgres-admin` and `redis-auth`.

## The deploy scripts, step by step

### `infra/infra.sh`

`infra/infra.sh up|down|status [postgres] [redis] [logs] [--delete-data]`; no component means all three, in that
order. It acts on the current `kubectl` context.

`up` first creates the namespace `infra` and, when the cluster has no metrics API, installs metrics-server (on Docker
Desktop with `--kubelet-insecure-tls`, because its kubelet's certificate does not name the node). Then, per component:

| Component | `up` | `down` |
| --- | --- | --- |
| `postgres` | the Secret `postgres-admin` with a random password if it does not exist → `kubectl apply -f postgres.yaml` → wait for the StatefulSet (up to 15 minutes: crash recovery takes as long as it takes). When `postgresql.conf` changed it prints the reload command (`SELECT pg_reload_conf()`) or, for `shared_buffers`, `max_connections` and `shared_preload_libraries`, the restart command | deletes the StatefulSet, the CronJob and its Jobs, the Service and the ConfigMap |
| `redis` | the Secret `redis-auth` with a random password if it does not exist → `kubectl apply -f redis.yaml` → restarts Redis when `redis.conf` changed (the cache starts empty) → waits | deletes the Deployment, the Service and the ConfigMap |
| `logs` | without `LOKI_URL`: the ConfigMap `grafana-dashboards` from `dashboards/` → `kubectl apply -f logs.yaml` → restarts Loki, Grafana and Alloy when a ConfigMap changed → waits. With `LOKI_URL`: the Secret `loki-external` (URL, `LOKI_USERNAME`, `LOKI_PASSWORD`), removes Loki and Grafana (Loki's PVC is kept), applies only Alloy (`-l app.kubernetes.io/name=alloy`), which pushes there. `up logs` without `LOKI_URL` switches back | deletes the workloads, Services, ConfigMaps, the ServiceAccount, the NetworkPolicy and the ClusterRole(Binding) |

**What `down` keeps.** The PersistentVolumeClaims (`data-postgres-0`, `postgres-backups`, `data-loki-0`) and the
password Secrets (`postgres-admin`, `redis-auth`, `loki-external`) stay, and `down` prints them; `up` brings the
component back with its data and passwords. `down <component> --delete-data` deletes them too, without asking.
For `postgres` that is the data, the backups and the administrator's password; the services' `database-credentials`
then name users that no longer exist, so run `create-database.sh` again for each service after the next
`up postgres`.

`up` never changes an existing password Secret: PostgreSQL stores its administrator's password when it first creates
its data directory. `status` lists the pods, Services and PVCs of `infra` and prints the port-forward and `exec`
commands of "Day-2 operations" below.

### `infra/create-database.sh`

`infra/create-database.sh <service-namespace> <database> [app-user] [migrator-user] [--rotate]`: one PostgreSQL for
the platform, one database per service. For the product-management API:

```bash
infra/create-database.sh product-management product_management pm_app pm_migrator
```

Without the user names it uses `<database>_app` and `<database>_migrator`. It runs `psql` inside the PostgreSQL pod
(`kubectl -n infra exec statefulset/postgres`), so nothing but `kubectl` is needed, and passes the passwords on
stdin. Idempotently, it:

1. creates the two users (if missing) and sets their passwords: the ones already in the service's Secret, or new
   random ones the first time or with `--rotate`;
2. sets the app user's guard rails: `statement_timeout = 30s`, `idle_in_transaction_session_timeout = 60s`;
3. creates the database (if missing), revokes it from `PUBLIC`, grants `CONNECT` to both users and `CREATE` to the
   migrator (for trusted extensions such as `pg_trgm`);
4. in the database: creates `pg_stat_statements`, makes the migrator the owner of the schema `public`, removes
   `CREATE` on it from `PUBLIC`, grants `USAGE` to the app user, and hands tables and sequences owned by anyone else
   (e.g. after a restore as the administrator) to the migrator;
5. writes the Secret `<service-namespace>/database-credentials` (`host`, `port`, `database`, `app-user`,
   `app-password`, `migrator-user`, `migrator-password`), creating the namespace if needed.

The users and the database are the platform's; what the app user may do on the service's tables is the service's own
migrations' business (here `GrantAppPermissions`). After `--rotate`, restart the service's pods so they read the new
Secret (it prints the command).

### `backend/deploy/deploy.sh`

`ENVIRONMENT` picks the overlay (`local` by default). It needs the shared PostgreSQL and Redis running and the
database created. Locally:

1. **Local secrets** (first run only): `backend/deploy/local-secrets.sh` creates what is missing in
   `backend/deploy/k8s/overlays/local/generated/` (signing key, demo consumers), and the API's .NET user-secrets for
   `dotnet run`.
2. **Image:** builds `backend/` → `product-management/api:<tag>` and publishes it to the cluster (below).
3. **The shared Secrets:** checks that `database-credentials` exists in `product-management` (if not, it stops and
   prints the `create-database.sh` command), and copies the password of `infra/redis-auth` into `redis-credentials`.
4. **Migrations:** runs that image once with `migrate`, in a one-off pod in the cluster, as `pm_migrator` (below).
5. **Rollout:** writes `backend/deploy/k8s/.build/<env>/kustomization.yaml` (not committed), which takes the overlay
   and pins this release's image with Kustomize `images`; renders it with `kubectl kustomize`, pipes it to
   `kubectl apply`, and waits for `deployment/api`.

| Variable | Effect |
| --- | --- |
| `ENVIRONMENT` | `local` (default), `staging` or `production` |
| `API_IMAGE` | an image already built and pushed (`registry/name:tag` or `name@sha256:…`): no local build |
| `MIGRATIONS` | `run` (default); `skip`: the pipeline ran them in its own job; `only`: migrate, no rollout (the pipeline's migrate job) |
| `MIGRATION_REDIS` | the Redis the migration invalidates (default `redis.infra.svc.cluster.local:6379`) |
| `SEED_SAMPLE_DATA` | sample catalogue on an empty database (default `true`) |

### `frontend/deploy/deploy.sh`

Locally: builds `frontend/` → `product-management/web:<tag>` (with `APP_VERSION=<tag>`, the release the browser
reports with its errors) and `frontend/gateway/` → `product-management/gateway:<tag>`, publishes both, writes
`frontend/deploy/k8s/.build/<env>/kustomization.yaml` with both images pinned, applies it and waits for both
rollouts. Variables: `ENVIRONMENT`, `WEB_IMAGE` and `GATEWAY_IMAGE` (no local build), and `LOGS=off`, which adds a
patch that sets the gateway's `COLLECTOR_UPSTREAM` to `""`: the gateway's `collector.sh` then makes `/collect` answer
204 and drop the reports instead of proxying to a collector that is not there.

**What a deploy does not do.** `kubectl apply` does not prune: an object removed from the manifests stays in the
cluster until it is deleted by hand (`kubectl delete <kind> <name>`), in each environment. Locally the old
`api-secrets-<hash>` Secrets also stay (they let `kubectl rollout undo` go back to the previous keys); delete old ones
with `kubectl -n product-management get secrets` and `kubectl delete secret`.

## Image tags

Each deploy script decides two things for local deployments (the pipelines tag images with the commit's short SHA
and push them to the project's registry):

* **The tag:** the commit's short hash (`git rev-parse --short HEAD`), plus `-dirty-<timestamp>` when the working tree
  has uncommitted changes, so two different builds never share a tag.
* **Where the image comes from:** on Docker Desktop (context `docker-desktop`) the cluster runs on the same Docker
  engine that built the image, so nothing is pushed. Any other context pulls from a registry, so the script needs the
  image references (`API_IMAGE`; `WEB_IMAGE` and `GATEWAY_IMAGE`), as the pipelines pass them, and stops with a
  message when they are missing.

## Migrations run before the rollout, with the image being deployed

`backend/deploy/deploy.sh` runs the API image being deployed once, with the argument `migrate`
([`ApiCommandLine.cs`](../../backend/ProductManagement.Api/Hosting/ApiCommandLine.cs)), as the schema owner, in a
one-off pod in the namespace `product-management`:

```bash
kubectl -n product-management run migrate-<tag> --rm --attach --restart=Never --quiet \
  --image=product-management/api:<tag> --overrides='{ "spec": { "containers": [{ "args": ["migrate"], "env": [ … ] }] } }'
#   DB_HOST, DB_PORT, DB_NAME, DB_USER   ← Secret database-credentials (host, port, database, migrator-user)
#   PGPASSWORD                           ← Secret database-credentials (migrator-password)
#   ConnectionStrings__ProductManagement = Host=$(DB_HOST);Port=$(DB_PORT);Database=$(DB_NAME);Username=$(DB_USER);GSS Encryption Mode=Disable
#   ConnectionStrings__Redis = $MIGRATION_REDIS,  Redis__Password ← Secret redis-credentials (password)
```

The kubelet assembles the connection string from the variables above it, so no password is on a command line or in
a manifest. The pod runs as non-root, without a service account token, and in staging and production pulls the image
with the registry Secret.

**Why a pod in the cluster.** The database is reachable only inside the cluster (its Service has no address outside
it). `kubectl run --rm --attach` streams the migration's output into the deploy script's terminal (or the pipeline's
job log), returns its exit code, so a failed migration stops the deployment before any API pod changes, and deletes
the pod when it ends: no Job manifest to keep, no port-forward, nothing left behind.

The migration waits for PostgreSQL (up to 30 attempts, 2 s apart), applies the EF Core migrations, seeds the sample
data into an empty database, and moves the Redis cache generations on (the data may have changed underneath the
cache). Then it exits, and only then is the new API rolled out. In the backend's GitLab pipeline the same step is the
`migrate:<environment>` job (`deploy/ci/deploy-environment.sh migrate`, which runs `deploy.sh` with
`MIGRATIONS=only`).

* **The code that migrates is exactly the code being deployed**, and it runs once per deployment, not once per pod.
  The API pods start with `ApplyMigrationsOnStartup=false` and read only `pm_app`'s password, which cannot change the
  schema. The migrator's password sits in the same namespace (`database-credentials`), read only by the migration pod.
* **Large indexes are built `CONCURRENTLY`** by the migrations that add them, and storage parameters (the per-table
  autovacuum settings of [ADR 0022](../adr/0022-database-at-scale.md)) are instant `ALTER TABLE … SET`, so the tables
  keep accepting writes.
* **It needs only the database** (and Redis, optionally). The `migrate` command builds a plain host with the
  infrastructure services, without the web server, authentication or rate limiting, so it is never given the key that
  signs access tokens.
* **The app user's rights are a migration too** (`GrantAppPermissions`): it runs as the schema owner, so its
  `ALTER DEFAULT PRIVILEGES` also covers the tables later migrations create. It uses the well-known name `pm_app`
  (a compiled migration cannot read configuration) and does nothing where that role does not exist (the tests'
  Testcontainers database).
* **Expand/contract.** The old pods keep running while the migration applies, and during the rollout old and new
  pods run side by side. A migration must therefore keep working with the version still running (add first, remove in
  a later release). A rollback never undoes a migration; a bad one is fixed forward.

## CI/CD pipelines

One GitLab pipeline per repository (backend, frontend, deploy), the only CI/CD: it tests every merge request and
delivers to staging and production. Every step it runs is a script in that repository that also runs on a laptop. The
deploy jobs reach the clusters through the GitLab agent for Kubernetes (`KUBE_CONTEXT`), so no cluster API is exposed
to the runners, or with a kubeconfig (a File variable `KUBECONFIG`) from a self-hosted runner next to the cluster.
The staging and production jobs exist only once the environment is switched on (project or group variables
`STAGING_ENABLED` / `PRODUCTION_ENABLED` = `true`); until then a pipeline on `main` stops, green, after scan (infra:
after validate). The backend's and frontend's deploy jobs store the project's deploy token (`gitlab-deploy-token`,
`read_registry`) as the Secret the pods pull the private images with (`imagePullSecrets` in the staging and production
overlays).

*Backend* ([`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| test | `test:backend` (`dotnet format --verify-no-changes`, Release build with warnings as errors, every test suite, the integration and acceptance tests with PostgreSQL and Redis from Testcontainers on Docker-in-Docker, JUnit), `test:manifests` (`deploy/ci/validate-manifests.sh` in the `alpine/k8s` image: render, `kubeconform -strict` against the Kubernetes 1.34 schemas, the settings each overlay must set, no Secret where there must be none), GitLab SAST and secret detection | merge requests, `main`, tags |
| build | `build:api` → `$CI_REGISTRY_IMAGE/api:<short sha>`; `migrations:sql` (`dotnet restore`, then the idempotent SQL of every migration, for review in the merge request) | merge requests, `main`, tags |
| scan | `scan:api`: Trivy, fails on fixable critical vulnerabilities | merge requests, `main`, tags |
| staging | `migrate:staging` (`deploy/ci/deploy-environment.sh migrate` in the `alpine/k8s` image: selects the cluster, the Secret `gitlab-registry-backend` from the deploy token, then the migration pod with the release's image, as `pm_migrator`) → `deploy:staging` (`deploy/ci/deploy-environment.sh deploy`: the same, plus `api-secrets` from CI/CD variables, then `deploy/deploy.sh` with the pipeline's image and `MIGRATIONS=skip`; restarts the API when `api-secrets` changed) | `main` with `STAGING_ENABLED`, automatic |
| verify | `verify:requirements` (`python3 scripts/check-requirements.py` against `STAGING_URL`) | `main` with `STAGING_ENABLED` |
| production | `migrate:production` (**manual**, protected environment) → `deploy:production` | `main` with `STAGING_ENABLED` and `PRODUCTION_ENABLED` |

*Frontend* ([`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| test | `test:frontend` (`npm run lint`, `format:check`, `test:ci`, `build`), `test:manifests` (the same checks on the web and gateway overlays), SAST and secret detection | merge requests, `main`, tags |
| build | `build:web`, `build:gateway` → `$CI_REGISTRY_IMAGE/{web,gateway}:<short sha>` | merge requests, `main`, tags |
| scan | `scan:web`, `scan:gateway` (Trivy) | merge requests, `main`, tags |
| staging | `deploy:staging` (`deploy/ci/deploy-environment.sh`: selects the cluster, the Secret `gitlab-registry-frontend` from the deploy token, then `deploy/deploy.sh` with the pipeline's images) | `main` with `STAGING_ENABLED`, automatic |
| verify | `verify:e2e` (Playwright, desktop and phone, against `STAGING_URL`; the browser-errors test is skipped, it reads Loki through a port-forward) | `main` with `STAGING_ENABLED` |
| production | `deploy:production` (**manual**, protected environment) | `main` with `STAGING_ENABLED` and `PRODUCTION_ENABLED` |

*Infra* ([`infra/.gitlab-ci.yml`](../../infra/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| validate | `validate:manifests` (`kubeconform -strict` on `postgres.yaml`, `redis.yaml`, `logs.yaml` and on the dashboards' ConfigMap as `infra.sh` renders it; `jq` on every dashboard; extracts the inline Alloy configuration), `validate:scripts` (`shellcheck` on `infra.sh`, `create-database.sh`, `add-node.sh`), `validate:alloy` (`alloy validate` and `alloy fmt` on that configuration) | merge requests, `main` |
| deploy | `deploy:staging`, `deploy:production` (**both manual**): select the cluster (`KUBE_CONTEXT` or `KUBECONFIG`) and run `./infra.sh up`; with the environment-scoped `LOKI_URL` (and `LOKI_USERNAME` / `LOKI_PASSWORD`) Alloy pushes to that Loki | `main` with `STAGING_ENABLED` / `PRODUCTION_ENABLED` |

The infra pipeline never creates a database: `create-database.sh` is a separate, deliberate step, once per service
and environment, run by an operator with access to the cluster.

Runners: every job of the three pipelines runs on gitlab.com's shared runners. None needs to reach the database:
the migrations run inside the cluster, and the jobs reach the cluster only through `kubectl`. The pipelines run on
gitlab.com; the staging and production jobs have not yet run against real clusters. Setting them up (the agent or a
kubeconfig, environments, every variable per project, or one machine of our own) is [chapter 12](12-from-zero.md).

## Day-2 operations

All commands run from the folder that holds the three repositories (`backend/`, `frontend/`, `infra/`) and assume
`kubectl config use-context docker-desktop`.

**See what is running**

```bash
kubectl -n product-management get pods -o wide
kubectl -n product-management get hpa
kubectl -n product-management top pods                       # needs metrics-server
kubectl -n product-management get events --sort-by=.lastTimestamp | tail -20
infra/infra.sh status                                        # the shared infrastructure: pods, Services, PVCs
```

**Reach the shared infrastructure from a laptop.** Nothing in `infra` is published outside the cluster; only the
application is (`http://localhost:8080`). For the rest:

```bash
kubectl -n infra port-forward svc/grafana 3000:3000          # Grafana → http://localhost:3000
kubectl -n infra port-forward svc/loki 3100:3100             # Loki's API → http://localhost:3100 (the browser-errors e2e test)
kubectl -n infra exec -it statefulset/postgres -- psql -U postgres -d product_management
kubectl -n infra exec -it deployment/redis -- redis-cli      # the password is already set in the pod (REDISCLI_AUTH)
```

**Logs.** In Grafana (through the port-forward, [chapter 8](08-observability.md)), or directly (this works with the
logs component down too):

```bash
kubectl -n product-management logs -l app.kubernetes.io/name=api --tail=20 --prefix
kubectl -n product-management logs -l app.kubernetes.io/name=gateway --tail=20   # access log: upstream pod, cache status, trace_id
kubectl -n infra logs statefulset/postgres --tail 50                            # slow statements, lock waits, errors
kubectl -n infra logs deployment/redis --tail 50
```

**Roll out a change.** Run the part's script again; it applies what changed:

```bash
backend/deploy/deploy.sh          # after changing the API or its configuration (deployment.yaml, api.yaml)
frontend/deploy/deploy.sh         # after changing the web app, the gateway or gateway.yaml
infra/infra.sh up postgres        # after changing postgres.yaml (postgresql.conf): then the reload or restart it prints
infra/infra.sh up redis           # after changing redis.yaml (restarts Redis: the cache starts empty)
infra/infra.sh up logs            # after changing logs.yaml or a dashboard
kubectl kustomize backend/deploy/k8s/overlays/local | less          # what would be applied
kubectl -n product-management rollout status deployment/api
```

**Change a setting.** Edit the env entry in `backend/deploy/k8s/base/deployment.yaml` (every environment) or in
`backend/deploy/k8s/overlays/<env>/api.yaml` (one environment), then deploy. To try a value without a deploy,
`kubectl -n product-management set env deployment/api RateLimiting__RequestsPerSecond=100` rolls the pods with it;
the next deploy puts the file's value back.

**Roll back** (each Deployment keeps 5 old ReplicaSets, each with its own image and configuration):

```bash
kubectl -n product-management rollout history deployment/api
kubectl -n product-management rollout undo deployment/api
kubectl -n product-management rollout undo deployment/gateway
```

A rollback does not undo database migrations; that is why migrations must stay compatible with the previous version.
In staging and production, roll back from GitLab instead: Operate → Environments → the environment → re-deploy an
earlier successful deployment, which re-runs that pipeline's deploy job with its images and manifests (and the
current Secrets).

**Rotate a secret.** Secrets are read when a pod starts, so every rotation ends with a restart of the pods that read
it.

* *The API's own keys* (`API_SIGNING_KEY`, `API_CONSUMERS`), staging and production: change the GitLab variable and
  re-run the backend's last deploy job. `create-secrets.sh` updates `api-secrets` and prints `api-secrets` when the
  content changed; `deploy-environment.sh` then runs `kubectl rollout restart deployment/api`. Locally:
  `FORCE=1 backend/deploy/local-secrets.sh` (a new signing key and consumer hashes: everyone signs in again), then
  `backend/deploy/deploy.sh`.
* *The database users' passwords* (any environment, from a machine with access to the cluster):

  ```bash
  infra/create-database.sh product-management product_management pm_app pm_migrator --rotate
  kubectl -n product-management rollout restart deployment/api
  ```

  The new passwords are set in PostgreSQL and in `database-credentials` at once. Open connections survive (PostgreSQL
  checks passwords only when a connection opens), but new connections of not-yet-restarted pods fail until the
  restart, so run the two together. The migrator's password is read by the next migration pod.
* *The Redis password:* a new random one, then Redis and every service that copies it:

  ```bash
  kubectl -n infra delete secret redis-auth
  infra/infra.sh up redis      # a new redis-auth, and restarts Redis with it (the cache starts empty)
  backend/deploy/deploy.sh     # copies it into redis-credentials and, as it changed, restarts the API
                               # (pipeline: re-run the migrate job, which copies it and restarts the API)
  ```

  Between the Redis restart and the API's, the API cannot reach Redis: the cache and the shared rate limits fall
  back, and idempotent writes answer 503 ([chapter 12](12-from-zero.md#rotate-secrets) has the order for production).

**Scale.** The HPAs own the replica counts, so `kubectl scale` is undone within seconds. Change the range instead
(for good: in `backend/deploy/k8s/base/hpa.yaml`, or `minReplicas`/`maxReplicas` of the gateway's HPA in
`frontend/deploy/k8s/base/gateway.yaml`):

```bash
kubectl -n product-management patch hpa api -p '{"spec":{"minReplicas":4,"maxReplicas":8}}'
kubectl -n product-management get hpa api -w
```

Keep `maxReplicas × (10 + 1)` connections (the request pool plus the readiness check's own connection) within what
PostgreSQL can serve: 8 × 11 = 88 of its 200, which other services' databases on the same server share
([chapter 6](06-scaling-and-capacity.md#why-the-database-is-the-ceiling)). More machines for the pods: on k3s,
`infra/add-node.sh <user@host> apps` ([chapter 12](12-from-zero.md)).

**Swagger UI** (enabled in the local and staging overlays)

```bash
kubectl -n product-management port-forward deployment/api 5080:8080   # http://localhost:5080/swagger, then Authorize with a token
```

**Database users.** `infra/create-database.sh` is idempotent: run it again after a restore, or when in doubt, and it
re-applies the users' passwords from the Secret, their guard rails and the schema's ownership. The app user's rights on
the tables come from the migrations: `backend/deploy/deploy.sh` re-applies them on an empty database.

**Backups and restores.** The CronJob `postgres-backup` dumps every database each night at 02:30 into the PVC
`postgres-backups` (`/backups/<date-time>/globals.sql` and `<database>.dump`), keeping 7 days. The dumps sit on the
same node as the data: they protect against mistakes (a dropped table, a bad migration), not against losing the node
or its disk; production ships them elsewhere ([chapter 12](12-from-zero.md)).

```bash
kubectl -n infra create job --from=cronjob/postgres-backup postgres-backup-now     # a backup now
kubectl -n infra logs job/postgres-backup-now                                      # what it wrote
kubectl -n infra get jobs -l app.kubernetes.io/name=postgres-backup               # the last runs
```

To restore one database, start a pod with the backups' volume and the administrator's password, and `pg_restore`
from it (`--clean --if-exists` drops and recreates the dump's objects, with their owners and grants):

```bash
kubectl -n infra apply -f - <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: postgres-restore
spec:
  restartPolicy: Never
  securityContext: { runAsUser: 70, runAsGroup: 70, fsGroup: 70 }
  tolerations:
    - { key: infra/role, operator: Equal, value: database, effect: NoSchedule }
  containers:
    - name: restore
      image: postgres:18-alpine
      command: [sleep, "3600"]
      env:
        - { name: PGHOST, value: postgres }
        - { name: PGUSER, value: postgres }
        - name: PGPASSWORD
          valueFrom: { secretKeyRef: { name: postgres-admin, key: password } }
      volumeMounts:
        - { name: backups, mountPath: /backups, readOnly: true }
  volumes:
    - name: backups
      persistentVolumeClaim: { claimName: postgres-backups }
EOF
kubectl -n infra wait --for=condition=Ready pod/postgres-restore
kubectl -n infra exec postgres-restore -- ls -l /backups
# No writes meanwhile: without its autoscaler (whose minimum cannot be 0), the API scales to zero
kubectl -n product-management delete hpa api && kubectl -n product-management scale deployment/api --replicas=0
kubectl -n infra exec postgres-restore -- \
  pg_restore -d product_management --clean --if-exists /backups/<date-time>/product_management.dump
kubectl -n infra delete pod postgres-restore
infra/create-database.sh product-management product_management pm_app pm_migrator   # ownership and passwords
backend/deploy/deploy.sh                    # the migration moves the cache generations on; the HPA is back
kubectl -n product-management scale deployment/api --replicas=2
```

Restoring into a new database first (`pg_restore -d <new database>` after `CREATE DATABASE`) leaves the current one
untouched until the dump has been checked, e.g. with `PG_DATABASE=<new database> backend/load-tests/verify-data.sh`.

**Reset the data.** One service's database (the others on the server stay):

```bash
kubectl -n infra exec statefulset/postgres -- psql -U postgres -c 'DROP DATABASE product_management WITH (FORCE)'
infra/create-database.sh product-management product_management pm_app pm_migrator   # empty database, same users
backend/deploy/deploy.sh                    # the migrations re-create the schema and the sample data
```

Everything in PostgreSQL, including its backups and the administrator's password:

```bash
infra/infra.sh down postgres --delete-data
infra/infra.sh up postgres
infra/create-database.sh product-management product_management pm_app pm_migrator
backend/deploy/deploy.sh
```

**Bulk data and load tests**

```bash
backend/load-tests/seed-bulk.sh                   # adds 850,000 products and ~5 million movements (minutes)
PRODUCTS=0 LEDGER_ROWS=20000000 backend/load-tests/seed-bulk.sh   # grows the ledger to 20 million rows (~20 minutes)
backend/load-tests/verify-data.sh                 # data invariants, all must be 0
backend/load-tests/run.sh smoke                   # any scenario: smoke, load, stress, spike, stock-consistency, rate-limit, teams, browse
```

The data scripts reach PostgreSQL with `kubectl -n infra exec -i statefulset/postgres -- psql -U postgres` and Redis
with `kubectl -n infra exec -i deployment/redis -- redis-cli` (override with `PSQL`, `REDIS_CLI`, `PG_USER`,
`PG_DATABASE`). `run.sh` raises the API's rate limits (and, for some scenarios, the gateway's) with `kubectl set env`
for the run, after saving the deployed values, and always sets those values back at the end. The autoscalers need
metrics-server, which `infra/infra.sh up` installs where it is missing.

**Troubleshooting**

| Symptom | Look at |
| --- | --- |
| API pods not ready | `kubectl -n product-management describe pod -l app.kubernetes.io/name=api`, then `kubectl -n product-management logs deployment/api`; readiness includes the database (`SELECT 1`), so check it: `infra/infra.sh status`, `kubectl -n infra logs statefulset/postgres --tail 50` |
| `deploy.sh` stops: "No Secret database-credentials" | the database was not created in this cluster: `infra/create-database.sh product-management product_management pm_app pm_migrator` |
| `deploy.sh` stops: "No Secret infra/redis-auth" | the shared Redis is not running: `infra/infra.sh up redis` |
| The API cannot reach the database or Redis | the connection strings it runs with: `kubectl -n product-management get deployment api -o jsonpath='{.spec.template.spec.containers[0].env}'`; whether the Services answer: `kubectl -n product-management run nettest --rm -it --restart=Never --image=busybox:1.37 -- nc -zv postgres.infra.svc.cluster.local 5432` (and `redis.infra.svc.cluster.local 6379`) |
| Password authentication failed | the users' passwords no longer match `database-credentials` (e.g. after `down postgres --delete-data`): `infra/create-database.sh …` again (it sets the Secret's passwords on the users), then `kubectl -n product-management rollout restart deployment/api`. Redis `WRONGPASS`: `backend/deploy/deploy.sh` copies `redis-auth` again, then restart the API |
| Migration step fails | its output in the deploy script's terminal (or the `migrate:*` job log); the pod is gone afterwards. A pod that never starts: `kubectl -n product-management get events --sort-by=.lastTimestamp` (image pull, missing Secret) |
| `postgres-0` Pending | `kubectl -n infra describe pod postgres-0`: its volume lives on one node, so the pod can only start there (the node is down, or tainted for another role) |
| `502` on `/api` | no ready API pod: `kubectl -n product-management get endpointslices -l kubernetes.io/service-name=api` |
| `502` on `/collect` in the browser console | no log collector: `infra/infra.sh up logs`, or deploy the frontend with `LOGS=off` |
| `ImagePullBackOff` | outside Docker Desktop, the image must be in a registry the cluster can pull from: the `API_IMAGE` / `WEB_IMAGE` / `GATEWAY_IMAGE` given to the deploy script, and the cluster's pull credentials |
| HPA shows `<unknown>` | metrics-server: `infra/infra.sh up` installs it where missing, then `kubectl top pods` |

## Where to look

* Deploy scripts: [`infra/infra.sh`](../../infra/infra.sh), [`infra/create-database.sh`](../../infra/create-database.sh),
  [`infra/add-node.sh`](../../infra/add-node.sh), [`backend/deploy/deploy.sh`](../../backend/deploy/deploy.sh),
  [`backend/deploy/local-secrets.sh`](../../backend/deploy/local-secrets.sh),
  [`frontend/deploy/deploy.sh`](../../frontend/deploy/deploy.sh)
* Backend manifests: [`backend/deploy/k8s/base`](../../backend/deploy/k8s/base)
  ([`deployment.yaml`](../../backend/deploy/k8s/base/deployment.yaml), [`service.yaml`](../../backend/deploy/k8s/base/service.yaml)),
  the overlays [`local`](../../backend/deploy/k8s/overlays/local/kustomization.yaml),
  [`staging`](../../backend/deploy/k8s/overlays/staging/api.yaml),
  [`production`](../../backend/deploy/k8s/overlays/production/api.yaml)
* Frontend manifests: [`frontend/deploy/k8s/base`](../../frontend/deploy/k8s/base), the overlays
  [`local`](../../frontend/deploy/k8s/overlays/local/gateway.yaml),
  [`staging`](../../frontend/deploy/k8s/overlays/staging/kustomization.yaml),
  [`production`](../../frontend/deploy/k8s/overlays/production/kustomization.yaml);
  [`gateway/collector.sh`](../../frontend/gateway/collector.sh)
* Shared infrastructure: [`infra/postgres.yaml`](../../infra/postgres.yaml), [`infra/redis.yaml`](../../infra/redis.yaml),
  [`infra/logs.yaml`](../../infra/logs.yaml), [`infra/dashboards`](../../infra/dashboards)
* Migrations: [`ApiCommandLine.cs`](../../backend/ProductManagement.Api/Hosting/ApiCommandLine.cs),
  [`DatabaseInitializer.cs`](../../backend/ProductManagement.Infrastructure/Persistence/DatabaseInitializer.cs),
  [`Persistence/Migrations`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations)
  (the app user's rights: [`GrantAppPermissions`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations/20261010164241_GrantAppPermissions.cs))
* CI/CD: [`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml), [`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml),
  [`infra/.gitlab-ci.yml`](../../infra/.gitlab-ci.yml), [`backend/deploy/ci`](../../backend/deploy/ci),
  [`frontend/deploy/ci`](../../frontend/deploy/ci)
* Each part on its own: [`backend/README.md`](../../backend/README.md), [`frontend/README.md`](../../frontend/README.md),
  [`infra/README.md`](../../infra/README.md)
* Decisions: [ADR 0021 deployment layout](../adr/0021-deployment-layout.md) (which supersedes most of
  [ADR 0011](../adr/0011-kubernetes-deployment.md)), [ADR 0006 PostgreSQL](../adr/0006-postgresql.md)
* Diagrams: [flows.md §9 deployment and CI/CD](../flows.md#9-deployment-and-cicd),
  [architecture.md](../architecture.md#deployment-topology)

## How to verify

```bash
infra/infra.sh up && infra/create-database.sh product-management product_management pm_app pm_migrator \
  && backend/deploy/deploy.sh && frontend/deploy/deploy.sh
infra/infra.sh status
kubectl -n infra get statefulset,deployment,cronjob,pvc
kubectl -n product-management get deploy,svc,hpa,pdb,networkpolicy,secret
kubectl -n product-management get svc api -o jsonpath='{.spec.clusterIP}'; echo      # None: headless
kubectl -n product-management get deploy api -o jsonpath='{.spec.template.spec.containers[0].image}'; echo   # the exact tag

# No password in the API's configuration: the connection strings have none, the passwords come from Secrets
kubectl -n product-management get deploy api -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}={.value}{"\n"}{end}' | grep -i -E 'connection|password'

# The API's database user cannot change the schema (its password goes over stdin, never on a command line)
kubectl -n product-management get secret database-credentials -o go-template='{{ index .data "app-password" | base64decode }}' \
  | kubectl -n infra exec -i statefulset/postgres -- sh -c 'PGPASSWORD="$(cat)" psql -h localhost -U pm_app -d product_management \
      -c "BEGIN" -c "DROP TABLE products" -c "ROLLBACK"'          # ERROR: must be owner of table products

# Every overlay renders and is valid (what CI runs)
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 backend/deploy/ci/validate-manifests.sh
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 frontend/deploy/ci/validate-manifests.sh
docker run --rm -v "$PWD/infra:/work:ro" -w /work alpine/k8s:1.34.11 \
  kubeconform -strict -summary -kubernetes-version 1.34.0 postgres.yaml redis.yaml logs.yaml

# Without logs: /collect answers 204
infra/infra.sh down logs && LOGS=off frontend/deploy/deploy.sh
curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:8080/collect -H 'Content-Type: application/json' -d '{}'   # 204
```
