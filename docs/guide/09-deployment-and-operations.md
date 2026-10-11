# 9. Deployment and operations

**After this chapter you will know** what runs where (everything in Kubernetes: the application in its namespace,
the shared PostgreSQL, Redis and logs in the namespace `infra`), how the system is split into three repositories
(backend, frontend, and deploy, which describes every environment), how the manifests are split into the apps and
one folder per environment, where the configuration and the passwords come from, what each script does step by step,
how and why the API applies its own migrations when it starts (as which database user, and safely with several
replicas), how releases work
(the app pipelines publish SemVer versions, the deploy repository pulls them in: GitOps with a pull model), how Argo
CD would take over, and the commands for day-to-day operation: rollouts,
rollbacks, secret rotation, scaling, backups and restores, troubleshooting, resetting data and
running load tests.

![What runs where: everything inside the Kubernetes cluster, the namespace product-management with the gateway, web and API, and the namespace infra with PostgreSQL, Redis, Loki, Grafana and Alloy](../images/kubernetes-map.svg)

---

## The layout: three parts

The system is in three GitLab repositories, each with its own pipeline: backend, frontend and deploy
([ADR 0021](../adr/0021-deployment-layout.md), revision of 2026-10-10). The backend and the frontend hold only code,
tests, Dockerfiles and the scripts their pipelines run; everything that describes what runs in a cluster is in the
deploy repository (GitOps):

```text
deploy/                             gitlab.com/zeiss/deploy: every environment, and the only pipeline that applies
├── apps/backend/                   the API, the same everywhere: namespace, deployment.yaml (configuration as env keys),
│                                   service.yaml (`api`, headless), hpa.yaml, pdb.yaml, network-policy.yaml
├── apps/frontend/                  web.yaml, gateway.yaml (the namespace object is only in apps/backend)
├── environments/local/             kustomization.yaml + api.yaml, gateway.yaml patches; the Secret api-secrets
│                                   generated from generated/ (gitignored), the images in generated/build (built tags,
│                                   or a published version's registry names, tag and pull Secret)
├── environments/staging/           kustomization.yaml (both apps, the `images:` with the chosen versions) + api.yaml,
├── environments/production/        gateway.yaml, registry.yaml patches and ingress.yaml
├── infra/                          the shared infrastructure, namespace `infra`, for every service on the cluster
│   ├── postgres.yaml               PostgreSQL 18: StatefulSet, Service, settings (postgresql.conf inline), nightly pg_dump
│   ├── redis.yaml                  Redis 8: Deployment, Service, settings (redis.conf inline)
│   ├── logs.yaml                   Loki, Grafana (data source, alert rules), Alloy (every pod's logs, the browser's errors)
│   └── dashboards/                 the two Grafana dashboards (JSON), loaded by infra.sh into a ConfigMap
├── argocd/                         prepared for Argo CD, not applied: applications/ (staging, production, ImageUpdater)
├── menu.sh                         the menu for people: numbered tasks over the scripts, a status line on top
├── scripts/                        what a person runs:
│   ├── setup-ubuntu.sh             a fresh Ubuntu server → single-node k3s (Docker Engine, kubectl, k3s)
│   ├── infra.sh                    infra.sh up|down|status [postgres] [redis] [logs] [--delete-data]
│   ├── deploy.sh                   deploy.sh <local|staging|production> [infra|apps|all] [--build | --version <X.Y.Z|latest>]
│   ├── versions.sh                 versions.sh <api|web|gateway|all>: the versions published in the registry
│   ├── set-version.sh              set-version.sh <env> <image|frontend|all> <version|latest>: choose a version
│   ├── add-node.sh                 joins a machine to a k3s cluster as a database, cache or apps node
│   ├── ci/                         what only the pipeline runs:
│   │   ├── validate.sh             renders and checks every environment, infra/ and argocd/ (the validate job)
│   │   ├── update-versions.sh      the pipeline's commits: staging to the newest versions, production as a merge request
│   │   └── smoke.sh                the smoke check of a deployed environment, through its public URL (by hand too)
│   └── lib/                        what the other scripts call:
│       ├── local-secrets.sh        the local signing key and demo consumers, generated once per machine
│       └── create-database.sh      one database per service, its two users, the Secret database-credentials
└── .gitlab-ci.yml

backend/                            gitlab.com/zeiss/backend: the API; ci/release-notes.sh, .gitlab-ci.yml
frontend/                           gitlab.com/zeiss/frontend: the web app, gateway/ (the gateway image), e2e/,
                                    ci/release-notes.sh, .gitlab-ci.yml
```

Locally the three sit side by side (the monorepo, or three clones), and `deploy/scripts/deploy.sh local` builds the
images from `../backend` and `../frontend` (`BACKEND_DIR`, `FRONTEND_DIR` to point elsewhere): what a developer wants
to see their changes in a cluster. With only the deploy repository (a server, a demo) it runs a **published version**
from the registry instead (`--version 1.0.0`, default `latest`), with a deploy token that may read the registry;
`--build` and `--version` force one or the other, and staging and production never use the checkouts
([the deploy README](../../deploy/README.md#where-the-images-come-from-backend-frontend-or-the-registry)). `deploy/menu.sh` offers all of it as a numbered menu. Deploy in this order; each step
only needs what the one before it started:

```bash
deploy/scripts/infra.sh up        # PostgreSQL, Redis, Loki + Grafana + Alloy in the namespace infra
deploy/scripts/deploy.sh local    # secrets (first run), the database (first run), 3 images, rollout (the API migrates)
                                  # → http://localhost:8080   (deploy.sh local all: both steps)
```

Every first `deploy.sh` to a cluster (local, staging or production) runs `create-database.sh` itself when the Secret
`database-credentials` is missing: the database and its users must exist before the API starts, because its
migrations (run by the API itself, as `pm_migrator`) create the tables.

**What connects the parts.** Only addresses and Secrets, written in the environment's files:

| From | To | Where it is set |
| --- | --- | --- |
| the API | PostgreSQL, Redis | `ConnectionStrings__ProductManagement` (`postgres.infra.svc.cluster.local:5432`, user `pm_app`) and `ConnectionStrings__Redis` (`redis.infra.svc.cluster.local:6379`) in `deploy/environments/<env>/api.yaml` |
| the API's migrations at startup | PostgreSQL | the same connection string, with the user and password of `Database__MigratorUsername` / `Database__MigratorPassword` (the Secret `database-credentials`, keys `migrator-user`, `migrator-password`) |
| the API | the database and Redis passwords | the Secrets `database-credentials` (made by `create-database.sh`; `app-password` for requests, the migrator's keys for the migrations) and `redis-credentials` (copied from `infra/redis-auth` by `deploy.sh`) |
| the gateway | the API | `API_UPSTREAM`, `JWKS_URL` in `deploy/environments/<env>/gateway.yaml`: the Service `api.product-management.svc.cluster.local:8080` |
| the gateway | the log collector (optional) | `COLLECTOR_UPSTREAM` in the same file: `alloy-faro.infra.svc.cluster.local:12347`, or `""` with `LOGS=off` |
| infra (Alloy) | Loki | the Loki in `infra` by default; `LOKI_URL` for any other Loki |

The namespace `product-management` is one object, in `apps/backend`, and every environment includes both apps.
`create-database.sh` creates it too if it is missing, so it can run before the first deployment.

**Base and environments.** `deploy/apps/<app>` holds everything that runs in the cluster, the same in every
environment; `deploy/environments/<env>` holds one `kustomization.yaml` for both apps and only what differs, as one
strategic-merge patch per Deployment (`api.yaml`, `gateway.yaml`) plus, in staging and production, the pull Secret
(`registry.yaml`), the Ingress and the released image tags. `kubectl kustomize` is built into `kubectl`, so nothing
else is installed. `deploy/scripts/ci/validate.sh` checks that every environment renders, is valid against the
Kubernetes schemas and sets what it must: the API's both connection strings, its migrations at startup as the
migrator (from `database-credentials`) and a sample-data setting (never on in production), the gateway's `API_UPSTREAM`,
`JWKS_URL` and `COLLECTOR_UPSTREAM`, no Secret outside `local` (and there only the generated `api-secrets`), the pull
Secret and registry images with a tag in staging and production. It checks `infra/` with `kubeconform` too.

**Configuration is plain environment variables in the Deployment.** The API's settings are `env` entries in
`deploy/apps/backend/deployment.yaml` (ASP.NET Core reads `Section__Key` as `Section:Key`): token lifetime, rate
limits, sign-in throttle, idempotency lifetimes, `Database__ApplyMigrationsOnStartup=true` with the migrator's user
and password from `database-credentials`, `OpenApi__Enabled`. An environment's `api.yaml` sets only what differs, and
always the two connection strings and `Database__SeedSampleData` (`true` locally and in staging, `false` in
production). Because the values are in the pod
template, changing one rolls the pods like a code change, and `kubectl rollout undo` restores the old configuration
with the old code. The gateway works the same way (`GATEWAY_RATE_LIMIT_*`, `GATEWAY_TRUSTED_PROXIES`, `JWT_*` in
`apps/frontend`; the addresses in the environments).

**Three Secrets, no password in a connection string or a pipeline variable.** The API reads:

| Secret | Key | Becomes in the API | Made by |
| --- | --- | --- | --- |
| `database-credentials` | `app-password` | `PGPASSWORD`: Npgsql uses it when the connection string has no password (the readiness check's own connection too) | `deploy/scripts/lib/create-database.sh`, once per environment: a random password, kept on every re-run |
| `redis-credentials` | `password` | `Redis__Password`: the API sets it on the Redis connection, over any password in the connection string | `deploy/scripts/deploy.sh` on every deploy: a copy of `infra/redis-auth`, because a pod can only read Secrets of its own namespace |
| `api-secrets` | `signing-key` | `Authentication__SigningKey`: the ES256 private key that signs tokens | locally `generated/signing-key.pem`; staging and production the deploy project's CI/CD variable `API_SIGNING_KEY` (File) |
| `api-secrets` | `consumers.json` | the file `/app/config/consumers.json`: client ids, PBKDF2 hashes, roles | locally `generated/consumers.json`; staging and production `API_CONSUMERS` (File) |

The database and Redis passwords are random, created in the cluster (`infra.sh` makes `postgres-admin` and
`redis-auth`, `create-database.sh` the users' passwords) and never leave it: no file, no pipeline variable holds
them. `api-secrets` holds only the API's own keys. Locally `deploy/scripts/lib/local-secrets.sh` writes the two files into
`deploy/environments/local/generated/` (gitignored, mode 600) once per machine, and the environment's
`secretGenerator` makes `api-secrets-<hash>` from them: a changed file gives the Secret a new name, which changes the
Deployment and rolls the pods. In staging and production the repository contains no Secret; `deploy/scripts/deploy.sh`
creates `api-secrets` from the deploy project's CI/CD variables, and the registry credential `gitlab-registry` from
the group deploy token.

**The shared infrastructure runs in the cluster.** PostgreSQL, Redis and the logs (Loki, Grafana, Alloy) run in
Kubernetes like the application, in one namespace, `infra`, shared by every service on the cluster: one PostgreSQL
with one database per service (`deploy/scripts/lib/create-database.sh`), one Redis (services separate their keys by prefix), and
one log pipeline that collects every pod's log without the service doing anything. The same files run on Docker
Desktop, on a single k3s machine and on a production cluster, where `deploy/scripts/add-node.sh` gives PostgreSQL and Redis
nodes of their own ([chapter 12](12-from-zero.md)). Kubernetes restarts, schedules and passes the passwords for them
as for any workload; their data is on PersistentVolumeClaims that `infra.sh down` never deletes
([ADR 0021, update](../adr/0021-deployment-layout.md)). A managed database or cache instead is a host name in the
environment's `api.yaml` (its comments say how, with TLS).

**Logs are optional.** The `logs` component (Loki, Grafana, Alloy) can be left out:
`deploy/scripts/infra.sh up postgres redis` and `LOGS=off deploy/scripts/deploy.sh local` (in staging or production,
`COLLECTOR_UPSTREAM: ""` in the environment's `gateway.yaml`). The application works the same; the pods log to stdout (`kubectl logs`) and the gateway answers the browser's error reports with 204. With
`LOKI_URL=… deploy/scripts/infra.sh up logs` the logs go to another Loki instead (no Loki or Grafana in the cluster).

## Where the local deployment runs

`deploy/scripts/infra.sh up` and `deploy/scripts/deploy.sh local` deploy the whole system on one machine, with images
built there or published ones pulled from the registry. Three platforms, the same scripts (plain bash with tools that
behave the same in their GNU and BSD versions; `.gitattributes` keeps the scripts' line endings LF on Windows):

| | macOS | Windows | Ubuntu server (22.04, 24.04) |
| --- | --- | --- | --- |
| Kubernetes | Docker Desktop's (context `docker-desktop`) | Docker Desktop's, WSL 2 backend (context `docker-desktop`) | k3s v1.34.1+k3s1, one node (context `default`) |
| Prepared by | enabling Kubernetes in Docker Desktop | the same, plus WSL integration for Ubuntu; the scripts run in the Ubuntu shell | `deploy/scripts/setup-ubuntu.sh`: Docker Engine 29.9.0, kubectl v1.34.1, k3s, `git`/`openssl`/`python3`/`curl`/`jq` (about 1.5 minutes) |
| How the images reach the cluster | built: the cluster runs on the Docker engine that built them; published: pulled with the Secret `gitlab-registry` | the same | built: `docker save … \| sudo k3s ctr images import -` (k3s has a containerd of its own; tags kept, `imagePullPolicy: IfNotPresent`); published: k3s pulls them itself |
| Gateway (`LoadBalancer`) | http://localhost:8080 | http://localhost:8080 | k3s' ServiceLB on the node's address: http://&lt;server-ip&gt;:8080 (and localhost on the server) |
| Volumes | `hostpath` | `hostpath` | `local-path` (a directory on the server; `postgres-backups` binds at the first backup) |
| metrics-server | installed by `infra.sh up` | the same | k3s' own (`infra.sh up` waits for it instead of installing a second) |
| NetworkPolicies | not enforced | not enforced | enforced (k3s' network policy controller) |
| Verified | every change | not run (scripts checked for macOS-only commands) | a fresh Ubuntu 24.04 VM (Multipass, 4 CPUs, 8 GB): setup, `infra.sh up`, `deploy.sh local`, smoke check, 14/14 requirement checks and 22/22 Playwright tests from the Mac, a start over from nothing; with only the deploy repository and `deploy.sh local --version 1.0.0`, the published path up to the pull: login, version check, `gitlab-registry`, k3s authenticated to the registry, but the 1.0.0 images are `linux/amd64` only and this VM is arm64, which `deploy.sh` now checks before it applies |

For images it builds, `deploy.sh local` decides how to deliver them from the context (`docker-desktop`) or from a
node that reports k3s (`v1.34.1+k3s1`) and is named like the machine it runs on; `LOCAL_CLUSTER=docker-desktop|k3s`
overrides it, and any other cluster is refused with an explanation (a remote cluster needs registry images: a
published version, or staging and production).
Two things the Ubuntu run brought to light and that are fixed for every platform: a commit hash made only of digits
(`1186184`) was a YAML number in the generated `kustomization.yaml` (the tags are quoted now), and
`crypto.randomUUID()`, which the web app used for idempotency keys, exists only in secure contexts (HTTPS or
`localhost`), so creating a product over `http://<server-ip>:8080` failed; the web app now falls back to
`crypto.getRandomValues()` (`shared/util/idempotency-key.ts`).

## What runs where

**Namespace `infra`** (the deploy repository's `infra/`, applied by `deploy/scripts/infra.sh up`):

| Object | Kind | Component | What it does |
| --- | --- | --- | --- |
| `postgres` | StatefulSet (1 pod, `postgres-0`) + Service | postgres ([`postgres.yaml`](../../deploy/infra/postgres.yaml)) | PostgreSQL 18 at `postgres.infra.svc.cluster.local:5432`, data on the PVC `data-postgres-0` (20 GiB), 4 CPUs and 3 GiB, 1 GiB `/dev/shm`, `max_connections=200`, `shared_buffers=768MB`, `pg_stat_statements` preloaded, slow statements (> 250 ms), lock waits, checkpoints and autovacuum runs logged; sized from the measurements in [ADR 0022](../adr/0022-database-at-scale.md). The settings are the ConfigMap `postgres-config`; a `preStop` hook makes a fast shutdown |
| `postgres-backup` | CronJob + PVC `postgres-backups` (20 GiB) | postgres | every night at 02:30: `pg_dumpall --globals-only` and `pg_dump -Fc` of every database into `/backups/<date-time>/`, 7 days kept (below, "Backups and restores") |
| `postgres-admin` | Secret | postgres | the superuser `postgres`'s random password, for `create-database.sh`, the backups and people; never for a service |
| `redis` | Deployment (1 pod, `Recreate`) + Service | redis ([`redis.yaml`](../../deploy/infra/redis.yaml)) | Redis 8 at `redis.infra.svc.cluster.local:6379`, password from the Secret `redis-auth` (`--requirepass`), memory only (no volume), `maxmemory 256mb`, `volatile-lru` (only keys with an expiry can be evicted), `FLUSHALL`/`FLUSHDB`/`DEBUG` disabled |
| `loki` | StatefulSet + Service | logs ([`logs.yaml`](../../deploy/infra/logs.yaml)) | Loki 3.5.5 at `loki:3100`, 14 days' retention, data on the PVC `data-loki-0` (10 GiB) |
| `grafana` | Deployment + Service | logs | Grafana 12.2.0 at `grafana:3000`, no volume: the Loki data source, the two dashboards ([`dashboards/`](../../deploy/infra/dashboards)) and the 12 alert rules are provisioned from ConfigMaps ([chapter 8](08-observability.md)); anonymous administrator, reached only through a port-forward |
| `alloy` | Deployment + ServiceAccount, ClusterRole `infra-alloy` (read pods and their logs) | logs | Grafana Alloy: reads the log of every pod in every namespace through the Kubernetes API (PostgreSQL, Redis, Loki and Grafana included) and pushes it to Loki as `{namespace, app}`; receives the browser's error reports at the Service `alloy-faro:12347`, which a NetworkPolicy (`alloy-ingress`) reserves for the gateway pods of `product-management` |

The shared infrastructure's own logs are `{namespace="infra", app="postgres"}` (or `redis`, `loki`, `grafana`,
`postgres-backup`).

**Database users** (made by [`create-database.sh`](../../deploy/scripts/lib/create-database.sh), one pair per service):
`pm_migrator` owns the schema `public` and is used only by the API's migration step at startup (on connections of its
own, closed when it ends); `pm_app` is the user of the API's requests, with
`CONNECT` and `USAGE` only. What `pm_app` may do on the tables is granted by the service's own migration
`GrantAppPermissions`, which runs as `pm_migrator`: read and write rows and draw ids from the sequences, on today's
tables and every later one (`ALTER DEFAULT PRIVILEGES`), nothing else (`CREATE TABLE`: "permission denied for schema
public"; `ALTER`, `DROP`: "must be owner"). A statement of `pm_app` is cancelled after 30 s and a transaction left
idle after 60 s. The superuser `postgres` is used by the infra scripts and by people, never by an application.

**Namespace `product-management`:**

| Object | Kind | App (made by) | What it does |
| --- | --- | --- | --- |
| `database-credentials` | Secret | platform (`create-database.sh`) | `host`, `port`, `database`, `app-user`, `app-password`, `migrator-user`, `migrator-password`. The API reads `app-password` (`PGPASSWORD`, its requests) and `migrator-user` / `migrator-password` (its migrations at startup). |
| `redis-credentials` | Secret | platform (`deploy.sh`) | The shared Redis' password, copied from `infra/redis-auth` on every deploy. |
| `api-secrets` (locally `api-secrets-<hash>`) | Secret | backend (local `secretGenerator` / `deploy.sh` from CI/CD variables) | The signing key and the consumers (above). |
| `gitlab-registry` | Secret (staging, production) | platform (`deploy.sh`) | The registry credential every pod pulls with: the group deploy token `gitlab-deploy-token` (`read_registry`), which reads both projects' registries. |
| `api` | Deployment (no fixed count) | backend | The API pods, with their configuration as env keys. Each new pod first applies the database migrations (below), then serves. Rolling update with `maxUnavailable: 0`, a 5 s `preStop` pause, a startup probe of up to 10 minutes (for a long migration), readiness and liveness probes, 5 old ReplicaSets kept. Pool of 10 database connections per pod, plus one for the readiness check. |
| `api` | Service (headless, `clusterIP: None`) | backend | **The API's only Service.** `api.product-management.svc.cluster.local` lists every ready API pod; the gateway balances over them per request and downloads the public signing key from it ([chapter 2](02-request-journey.md#why-the-apis-service-is-headless)). |
| `api` | HorizontalPodAutoscaler | backend | 2–8 pods at 60 % CPU ([chapter 6](06-scaling-and-capacity.md)). |
| `api` | PodDisruptionBudget | backend | At least one pod stays up during node maintenance. |
| `api-ingress` | NetworkPolicy | backend | Only the gateway may connect. |
| `web` | Deployment (2 pods) + Service + PDB | frontend | nginx serving the Angular build. |
| `web-ingress` | NetworkPolicy | frontend | Only the gateway may connect. |
| `gateway` | Deployment (no fixed count) + PDB + HPA | frontend | The nginx gateway, 2–6 pods at 70 % CPU. |
| `gateway` | Service | frontend | Port 8080. Locally type `LoadBalancer` (`localhost:8080` on Docker Desktop, the server's address on k3s). |
| `gateway` | Ingress | frontend (staging, production: `environments/<env>/ingress.yaml`) | HTTPS for `staging.products.example.com` / `products.example.com`, certificate from cert-manager (Let's Encrypt). |

**`kube-system`:** metrics-server, which feeds the two HPAs and `kubectl top`. k3s and most managed clusters ship
it; where it is missing (Docker Desktop), `deploy/scripts/infra.sh up` installs it (v0.8.0).

The application namespace holds no data: everything that must survive is in `infra`, on the PVCs `data-postgres-0`,
`postgres-backups` and `data-loki-0`, and in the Secrets `postgres-admin` and `redis-auth`.

## The deploy scripts, step by step

The deploy repository keeps few files in view: `menu.sh` (a numbered menu that shows each command before it runs it),
six scripts a person runs in `scripts/`, the three only the pipeline runs in `scripts/ci/`, and the two the others call
in `scripts/lib/` (`create-database.sh` is still run by hand for another service, or with `--rotate`).

### `deploy/scripts/infra.sh`

`deploy/scripts/infra.sh up|down|status [postgres] [redis] [logs] [--delete-data]`; no component means all three, in
that order. It applies the files in `deploy/infra/` to the current `kubectl` context. `deploy.sh <env> infra` runs
`infra.sh up` (the deploy pipeline's manual `infra:<env>` jobs).

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

### `deploy/scripts/lib/create-database.sh`

`deploy/scripts/lib/create-database.sh <service-namespace> <database> [app-user] [migrator-user] [--rotate]`: one PostgreSQL for
the platform, one database per service. For the product-management API:

```bash
deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator
```

`deploy.sh local` runs exactly this on the first deploy (when `database-credentials` is missing); in staging and
production nothing runs it for you: an operator with access to the cluster runs it once per environment. Without the user names it uses `<database>_app` and `<database>_migrator`. It runs `psql` inside the PostgreSQL pod
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

### `deploy/scripts/deploy.sh`

`deploy/scripts/deploy.sh <local|staging|production> [infra|apps|all] [--build | --version <X.Y.Z|latest>]`; `apps`
(the default) is the API, the web app and the gateway, `infra` runs `infra.sh up`, `all` both. It selects the cluster
first (`KUBE_CONTEXT`, else the current context of `KUBECONFIG`), refuses to build images for a cluster that cannot run
them (only Docker Desktop's and a k3s on this machine can) and staging or production on `docker-desktop`. It needs the
shared PostgreSQL and Redis running.

**`local`** builds and applies what is in the sibling checkouts, or applies a published version. It prints which
(`Images: built from ../backend and ../frontend`, or `Images: published 1.0.0 from registry.gitlab.com`):

0. **Published only, before anything changes:** the registry login (`REGISTRY_USER` / `REGISTRY_PASSWORD`, else
   `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD`, else `docker login registry.gitlab.com`), and `versions.sh` to check the
   version exists for api, web and gateway (`latest`: the newest `X.Y.Z` all three have). Missing: it stops and lists
   what is published.

1. **Local secrets** (first run only): `deploy/scripts/lib/local-secrets.sh` creates what is missing in
   `deploy/environments/local/generated/` (signing key, demo consumers), and the API's .NET user-secrets for
   `dotnet run` when `../backend` is there.
2. **Images:** built: `../backend` → `product-management/api:<tag>`, `../frontend` → `product-management/web:<tag>`
   (with `APP_VERSION=<tag>`, the release the browser reports with its errors) and `../frontend/gateway` →
   `product-management/gateway:<tag>` with Docker; on k3s copies them into its containerd
   (`docker save | sudo k3s ctr images import -`). Either way it writes the images into the Kustomize Component
   `deploy/environments/local/generated/build/` (not committed): the built tags, or the registry names, the version
   and `imagePullSecrets: gitlab-registry` of a published version. The committed overlay stays the same for both, and
   `kubectl diff -k deploy/environments/local` compares with exactly what was applied.
3. **The shared Secrets:** published only: the pull Secret `gitlab-registry` from the login (the cluster pulls the
   images itself, Docker Desktop and k3s alike). Creates the database the first time (`create-database.sh`, when
   `database-credentials` is missing in `product-management`), and copies the password of `infra/redis-auth` into
   `redis-credentials`.
4. **Rollout:** `kubectl apply -k deploy/environments/local`, then waits for `api` (up to 15 minutes), `web` and
   `gateway`; restarts the API when one of its Secrets changed. The first new API pod applies the migrations as
   `pm_migrator` before it serves (below).

**`staging`, `production`** apply what is committed, nothing else (the deploy pipeline's `deploy:<env>` jobs):

1. renders `deploy/environments/<env>` and **refuses** it while an image still has `newTag: unreleased` (the
   placeholder until a version is chosen): release a version in backend and frontend and set it
   (`set-version.sh`, or `update:staging`) first;
2. checks that `database-credentials` exists (if not, it stops and prints the `create-database.sh` command);
3. creates the Secret `gitlab-registry` from `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD` (the group deploy token) and
   `api-secrets` from the files `API_SIGNING_KEY` and `API_CONSUMERS`, when they are set (otherwise it keeps the
   existing ones, and stops if there are none);
4. copies `infra/redis-auth` into `redis-credentials`;
5. applies the rendered environment, waits for the three rollouts and restarts the API when one of its Secrets
   changed. The new API pods migrate the database as they start; a failed migration keeps them from becoming ready,
   the rollout stops with the old pods serving, and the job fails at the wait.

| Variable | Effect |
| --- | --- |
| `KUBE_CONTEXT` | the kubectl context (the GitLab agent's `<project>:<agent>`); default: the current context |
| `BACKEND_DIR`, `FRONTEND_DIR` | local: the checkouts to build (default `../backend`, `../frontend`); when both exist, `local` builds |
| `REGISTRY_USER`, `REGISTRY_PASSWORD` | local, published: a deploy token with `read_registry` (a server: a group deploy token of its own, e.g. `server-pull`) → Secret `gitlab-registry`. The images must be built for the nodes' platform (`versions.sh all --platforms <version>`; 1.0.0 is `linux/amd64` only, so an ARM machine builds from source) |
| `LOCAL_CLUSTER` | local: `docker-desktop` or `k3s`, when the detection is wrong |
| `LOGS=off` | local: a patch that sets the gateway's `COLLECTOR_UPSTREAM` to `""`: its `collector.sh` then makes `/collect` answer 204 and drop the reports instead of proxying to a collector that is not there |
| `CI_DEPLOY_USER`, `CI_DEPLOY_PASSWORD`, `CI_REGISTRY` | staging, production: the registry credential (GitLab passes the group deploy token by itself) |
| `API_SIGNING_KEY`, `API_CONSUMERS` | staging, production: files with the API's ES256 private key (PEM) and `consumers.json` |

### `deploy/scripts/ci/validate.sh` and `deploy/scripts/ci/smoke.sh`

`validate.sh` renders `environments/local` (twice: with built and with published images), `staging` and
`production` and checks them (`kubeconform -strict`
against the Kubernetes 1.34 schemas, the labels, the settings each must set, no Secret where there must be none),
then `infra/` the same way; it never touches a cluster. `smoke.sh <base url>` checks a deployed environment from
outside, through the gateway, retrying each step for up to a minute: `GET /healthz` 200, `GET /` 200 with the web
app, and `POST /api/auth/token` with an unknown client, 401 `invalid_client` (the gateway reaches the API; the API's
`/health/ready` is not routed through the gateway). With `SMOKE_CLIENT_ID` and `SMOKE_CLIENT_SECRET` (a viewer
consumer) it also signs in and reads `GET /api/categories`. Both run on a laptop too:
`deploy/scripts/ci/smoke.sh http://localhost:8080`.

**What a deploy does not do.** `kubectl apply` does not prune: an object removed from the manifests stays in the
cluster until it is deleted by hand (`kubectl delete <kind> <name>`), in each environment. Locally the old
`api-secrets-<hash>` Secrets also stay (they let `kubectl rollout undo` go back to the previous keys); delete old ones
with `kubectl -n product-management get secrets` and `kubectl delete secret`.

## Clean deploy

**Locally, from nothing.** This removes the application, the shared infrastructure with its volumes and passwords,
and the local keys, then deploys again; an empty database gets the 24-product sample catalogue from the migrations.

```bash
kubectl config use-context docker-desktop          # on the Ubuntu server (k3s): default
kubectl delete namespace product-management --ignore-not-found
deploy/scripts/infra.sh down --delete-data
rm -rf deploy/environments/local/generated
deploy/scripts/infra.sh up
deploy/scripts/deploy.sh local
deploy/scripts/ci/smoke.sh http://localhost:8080
```

Keeping the data and rebuilding only the application is just the last two commands. On the Ubuntu VM the whole block
took 103 s. With only the deploy repository, the deploy line is
`REGISTRY_USER=… REGISTRY_PASSWORD=… deploy/scripts/deploy.sh local --version 1.0.0`; the menu's item 9 runs the whole
block after you type `delete everything`.

**A new environment, the first time:** cluster (k3s, nodes joined with `add-node.sh`), the GitLab variables and the
group deploy token, `infra:<environment>` (the first deploy creates the database), `STAGING_ENABLED` / `PRODUCTION_ENABLED` and the
`update:staging` schedule, then a first version tag in backend and in frontend; the deploy pipeline refuses the
`unreleased` placeholders until every image has a version. The
step-by-step list is in the deploy repository's README ("A new environment, the first time") and, with every variable, in
[chapter 12](12-from-zero.md).

## Image tags

* **Local:** `deploy.sh local` tags each image with its checkout's short commit hash (`git rev-parse --short HEAD`),
  plus `-dirty-<timestamp>` when the working tree has uncommitted changes, so two different builds never share a tag.
  On Docker Desktop (context `docker-desktop`) the cluster runs on the same Docker engine that built the images, so
  nothing is pushed; on k3s they are copied into its containerd with their tags. That is why `local` runs only on
  those two. The tags are written quoted (`newTag: "1186184"`): a hash made only of digits is otherwise a YAML
  number. A folder that is not a git checkout gets `dev-<timestamp>`. A published version (`deploy.sh local
  --version 1.0.0`) runs the release tags of the registry instead, like staging.
* **Builds:** every app pipeline tags its images with the commit's short SHA and pushes them to the project's
  registry (`registry.gitlab.com/zeiss/backend/api`, `.../frontend/web`, `.../frontend/gateway`). These are candidates:
  no environment runs a commit build by itself.
* **Versions (staging and production):** a git tag `vX.Y.Z` (SemVer, optionally `-rc.N`) on a commit of `main` makes
  the tag pipeline add the tag `X.Y.Z` (without the `v`) to the same image after its tests and scan passed
  (`crane tag`: same digest), and create a GitLab Release. The version that runs is the `newTag` in
  `deploy/environments/<env>/kustomization.yaml`, set by `deploy/scripts/set-version.sh` (for staging by the scheduled
  `update:staging`); nothing else decides it. A version is never overwritten: the publish jobs refuse to retag an existing `X.Y.Z` with another image.

## The API applies its own migrations when it starts

Every API pod, before it serves its first request, brings the database schema up to date
([`DatabaseMigrator.cs`](../../backend/ProductManagement.Infrastructure/Persistence/DatabaseMigrator.cs), called from
`Program.cs` before `app.RunAsync()`). There is no migration command, job or pod: wherever the image runs (local,
staging, production; with `kubectl apply`, Argo CD or `docker run`), the schema comes from exactly the code that
starts. `deploy.sh` only makes sure the database and its users exist first (`create-database.sh`).

```text
new api pod starts
  ├─ wait for PostgreSQL (up to 30 attempts, 2 s apart)
  ├─ connect as pm_migrator (Database__MigratorUsername / Database__MigratorPassword, from database-credentials),
  │  on a short-lived, unpooled connection of its own; requests later use pm_app (PGPASSWORD)
  ├─ take the migration lock: pg_try_advisory_lock(<key>), asked again every 2 s while another pod holds it
  ├─ MigrateAsync: each pending migration commits on its own, recorded in __ef_migrations_history;
  │  then the sample data (Database__SeedSampleData, only into an empty table); then the cache generations move on
  ├─ log "Database is up to date (N migrations applied as pm_migrator, 0.4 s)", release the lock (close the connection)
  └─ start the HTTP server → the startup probe passes → readiness → traffic
```

* **One replica at a time.** During a rollout or a scale-up several pods start together. The first takes a
  PostgreSQL **advisory lock** (a lock on a number, held by its session) and migrates; the others log "Another
  instance is migrating the database; waiting for it to finish", ask again every 2 s, and then find nothing left to
  do. If the migrating pod dies, its session ends and PostgreSQL releases the lock. EF Core's own migration lock is not
  enough here: the Npgsql provider implements it as `LOCK TABLE __ef_migrations_history` inside each migration's
  transaction, so it is released between two migrations, and a second pod can slip in and apply one again (we measured
  a duplicate key in the history). The waiting pods poll instead of blocking in `pg_advisory_lock`, because a blocked
  statement holds a snapshot that `CREATE INDEX CONCURRENTLY` in the migrating pod would wait for (both would wait
  forever; measured too). An integration test starts two API instances at once on an empty database: both start, and
  the schema is migrated once.
* **Least privilege.** Only the migration step connects as `pm_migrator`, the schema owner; requests use `pm_app`,
  which cannot change the schema. Both passwords come from `database-credentials`; neither is in a connection string.
* **Serving waits for the schema.** The HTTP server starts only after the migrations, so `/health/ready` cannot
  answer before: the pod gets no traffic until its schema is there. The startup probe allows 10 minutes
  (`periodSeconds: 5`, `failureThreshold: 120`) for a long migration or the wait for another pod's; the migration's
  statements and the wait are bounded by the same 10 minutes.
* **A failed migration stops the rollout, not the service.** The new pod exits and restarts (`CrashLoopBackOff`),
  never becomes ready, and with `maxUnavailable: 0` every old pod keeps serving. `deploy.sh` (and the `deploy:<env>`
  job) fails at the rollout wait; `kubectl -n product-management logs deployment/api` shows the error.
* **Large indexes are built `CONCURRENTLY`** by the migrations that add them, and storage parameters (the per-table
  autovacuum settings of [ADR 0022](../adr/0022-database-at-scale.md)) are instant `ALTER TABLE … SET`, so the tables
  keep accepting writes while the old pods serve.
* **The app user's rights are a migration too** (`GrantAppPermissions`): it runs as the schema owner, so its
  `ALTER DEFAULT PRIVILEGES` also covers the tables later migrations create. It uses the well-known name `pm_app`
  (a compiled migration cannot read configuration) and does nothing where that role does not exist (the tests'
  Testcontainers database).
* **Expand/contract.** During the rollout the old and the new pods run side by side on the new schema. A migration
  must therefore keep working with the version still running (add first, remove in a later release). A rollback never
  undoes a migration; a bad one is fixed forward. The SQL of each version is the backend pipeline's `migrations:sql`
  artifact, reviewed in the merge request.

Why at startup and not as a separate step (what this project did before, a one-off `migrate` pod that `deploy.sh`
ran before applying): one path for every environment and every way of running the image, nothing to forget or to keep
in step with the image tag, and no Argo CD hook to maintain. The two reasons a separate step is usually preferred,
replicas racing and the app needing schema rights, are handled by the advisory lock and the separate migrator
credentials ([ADR 0021](../adr/0021-deployment-layout.md), revision of 2026-10-11).

## CI/CD pipelines

One GitLab pipeline per repository (backend, frontend, deploy), the only CI/CD. The flow is GitOps with a **pull**
release model: the backend and frontend pipelines test, build and scan their images and, for a version tag, *publish*
the version (`X.Y.Z`) and a GitLab Release; they never touch the deploy repository or a cluster. The deploy
repository's pipeline pulls versions in (a schedule for staging, a merge request for production), applies what its
`main` holds (the new API pods migrate the database as they start), and smoke-checks it.

```text
backend   tag v1.4.0 → test → build → scan → publish:api (api:1.4.0) → release (GitLab Release v1.4.0)
frontend  tag v2.1.0 → test → build → scan → publish:web, publish:gateway (2.1.0) → release
                                               │ the registry
                                               ▼
deploy    schedule: update:staging (newest X.Y.Z) ── commit "staging: api 1.4.0, web 2.1.0, gateway 2.1.0" to main
          main: environments/staging changed → deploy:staging (apply; the API migrates) → smoke:staging

deploy    propose:production (manual) or set-version.sh production … ── merge request "production: api 1.4.0, …"
                                                                          ▼  (merged by a person)
          main: environments/production changed → deploy:production (manual: apply; the API migrates) → smoke:production
```

Why pull and not push (the previous design, where each app pipeline committed its new tag into the deploy repository
with a write token of it): the app repositories need no credential of the deploy repository or of any cluster, so a
compromised or mistaken app pipeline cannot change an environment; an environment runs named versions with release
notes, not commit hashes; upgrade and downgrade are one command; and it is the model Argo CD Image Updater implements,
so moving to Argo CD changes who does each step, not the flow ([ADR 0021](../adr/0021-deployment-layout.md)).

Every step a pipeline runs is a script in that repository that also runs on a laptop. The jobs that touch a cluster
(the deploy repository's deploy and infra jobs) reach it through the GitLab agent for Kubernetes (`KUBE_CONTEXT`), so
no cluster API is exposed to the runners, or with a kubeconfig (a File variable `KUBECONFIG`). The staging and
production jobs exist only once the environment is switched on (deploy project or group variables `STAGING_ENABLED` /
`PRODUCTION_ENABLED` = `true`); until then a pipeline on `main` stops, green, after validate. The pods pull the
private images with the Secret `gitlab-registry`, made from the zeiss group's deploy token `gitlab-deploy-token`
(`read_registry`), which reads both registries; `versions.sh` lists the published versions with the same token.

*Backend* ([`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| test | `test:backend` (`dotnet format --verify-no-changes`, Release build with warnings as errors, every test suite, the integration and acceptance tests with PostgreSQL and Redis from Testcontainers on Docker-in-Docker, JUnit), GitLab SAST and secret detection | merge requests, `main`, version tags |
| build | `build:api` → `$CI_REGISTRY_IMAGE/api:<short sha>` (a tag pipeline reuses the image `main` built); `migrations:sql` (`dotnet restore`, then the idempotent SQL of every migration, for review in the merge request) | merge requests, `main`, version tags |
| scan | `scan:api`: Trivy, fails on fixable critical vulnerabilities | merge requests, `main`, version tags |
| publish | `publish:api` (`crane tag` in the `crane/debug` image: `api:<short sha>` also becomes `api:X.Y.Z`; the digest goes to `release` as a dotenv variable) | tags `vX.Y.Z[-rc.N]` only |
| release | `release` (GitLab's `release:` keyword, `release-cli` image: the Release `vX.Y.Z` with the image and digest and `git log` since the previous version, `ci/release-notes.sh`) | tags `vX.Y.Z[-rc.N]` only |

*Frontend* ([`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| test | `test:frontend` (`npm run lint`, `format:check`, `test:ci`, `build`), SAST and secret detection | merge requests, `main`, version tags |
| build | `build:web`, `build:gateway` → `$CI_REGISTRY_IMAGE/{web,gateway}:<short sha>` | merge requests, `main`, version tags |
| scan | `scan:web`, `scan:gateway` (Trivy) | merge requests, `main`, version tags |
| publish | `publish:web`, `publish:gateway` (both need every test and scan: a version is published for both or for none) | tags `vX.Y.Z[-rc.N]` only |
| release | `release` (both images and digests, the commits since the previous version) | tags `vX.Y.Z[-rc.N]` only |

The end-to-end tests (Playwright, `frontend/e2e/`) are not a pipeline job: they need the whole running system. Run
them by hand against an environment (`BASE_URL=<environment url> npm run e2e`, [`frontend/README.md`](../../frontend/README.md)).

*Deploy* ([`deploy/.gitlab-ci.yml`](../../deploy/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| validate | `validate:manifests` (`scripts/ci/validate.sh`: every environment, `infra/` and `argocd/`; extracts the inline Alloy configuration), `validate:scripts` (`shellcheck` on `menu.sh` and `scripts/`, `scripts/ci/`, `scripts/lib/`), `validate:alloy` (`alloy validate` and `alloy fmt` on that configuration) | merge requests, `main` (not schedules) |
| staging | `update:staging` (`scripts/ci/update-versions.sh staging`: the newest `X.Y.Z` of each image, committed to `main` with the job's own token, then applied and smoke-checked in the same job, since a job-token push starts no pipeline); `deploy:staging` (`scripts/deploy.sh staging apps`: apply, wait for the rollouts) → `smoke:staging` (`scripts/ci/smoke.sh $STAGING_URL`), automatic when a commit changes `environments/staging`, `apps/`, `infra/`, `scripts/deploy.sh` or `scripts/lib/`; `infra:staging` (**manual**, `scripts/deploy.sh staging infra`) | `update:staging`: the pipeline schedule (or manual on `main`); the others `main` with `STAGING_ENABLED` |
| production | `propose:production` (**manual**: the merge request with staging's versions); `deploy:production` (**manual**, protected environment) → `smoke:production`, when a commit changes `environments/production`, `apps/` or `scripts/deploy.sh`; `infra:production` (**manual**) | `main` with `PRODUCTION_ENABLED` |

With the environment-scoped `LOKI_URL` (and `LOKI_USERNAME` / `LOKI_PASSWORD`) the infra jobs make Alloy push to that
Loki. No pipeline creates a database: `create-database.sh` is a separate, deliberate step, once per service and
environment, run by an operator with access to the cluster.

**Version changes and rollbacks are commits.** `git log environments/staging` in the deploy repository is staging's
deployment history (one commit per change: "staging: api 1.4.0"); production changes only through merged merge requests,
so merging is the release decision. A rollback is the same command with an older version (`scripts/set-version.sh
production api 1.3.2`, through a merge request; or `git revert` of the version commit): the pipeline applies the older
images (their migrations are already applied: nothing to do). For staging, deactivate the schedule first, or
`update:staging` moves it forward again. Migrations are never rolled back (expand/contract, above).

**Argo CD, prepared.** `deploy/argocd/` holds what takes over from the pipeline once Argo CD runs in the cluster, and
`validate.sh` checks it on every change: an Application per environment (staging: automated sync with prune and
selfHeal; production: manual sync), Argo CD Image Updater's annotations on the staging Application (semver strategy,
only `X.Y.Z`, git write-back of the new `newTag` to `main`: exactly what `update:staging` does). No migration hook is
needed: the pods Argo CD rolls out migrate the database themselves. Production gets no
Image Updater on purpose: its versions stay a reviewed decision. The switch is a short procedure (install pinned Argo
CD and Image Updater, register the repository with a read token, create the credentials, switch off `deploy:*` and
`update:staging`): the deploy README, "Moving to Argo CD".

Runners: every job of the three pipelines runs on gitlab.com's shared runners. None needs to reach the database: the
API migrates it from inside the cluster, and the jobs reach the cluster only through `kubectl`. The pipelines run on
gitlab.com; the staging and production jobs have not yet run against real clusters. Setting them up (the agent or a
kubeconfig, environments, every variable per project, the tokens, or one machine of our own) is
[chapter 12](12-from-zero.md).

## Day-2 operations

All commands run from the folder that holds the three repositories (`backend/`, `frontend/`, `deploy/`) and the local
`tools/`, and assume `kubectl config use-context docker-desktop`.

**See what is running**

```bash
kubectl -n product-management get pods -o wide
kubectl -n product-management get hpa
kubectl -n product-management top pods                       # needs metrics-server
kubectl -n product-management get events --sort-by=.lastTimestamp | tail -20
deploy/scripts/infra.sh status                               # the shared infrastructure: pods, Services, PVCs
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

**Roll out a change.** Locally, run the script again; it applies what changed:

```bash
deploy/scripts/deploy.sh local            # after changing the API, the web app, the gateway or their manifests
deploy/scripts/infra.sh up postgres       # after changing postgres.yaml (postgresql.conf): then the reload or restart it prints
deploy/scripts/infra.sh up redis          # after changing redis.yaml (restarts Redis: the cache starts empty)
deploy/scripts/infra.sh up logs           # after changing logs.yaml or a dashboard
kubectl diff -k deploy/environments/local                    # what differs from the last deploy (nothing, after one)
kubectl -n product-management rollout status deployment/api
```

In staging and production a change is a commit: code goes through a version tag in its repository (published by its
pipeline, pulled into staging by `update:staging`, into production by a merge request), a manifest or setting through
a merge request in the deploy repository, whose pipeline applies it (`infra/` through the manual `infra:<env>` jobs).

**Choose a version.** From a clone of the deploy repository (needs `curl`, `jq`, `yq` and a token with
`read_registry`, or a `docker login registry.gitlab.com`):

```bash
deploy/scripts/versions.sh all                         # what is published, newest first
deploy/scripts/set-version.sh production api 1.4.0     # edits environments/production/kustomization.yaml, prints the diff
git switch -c production/api-1.4.0 && git commit -am "production: api 1.4.0" && git push -u origin HEAD   # then the merge request
```

**Change a setting.** Edit the env entry in `deploy/apps/backend/deployment.yaml` (every environment) or in
`deploy/environments/<env>/api.yaml` (one environment), then deploy (locally `deploy.sh local`; elsewhere merge it
into the deploy repository's `main`). To try a value without a deploy,
`kubectl -n product-management set env deployment/api RateLimiting__RequestsPerSecond=100` rolls the pods with it;
the next deploy puts the file's value back.

**Roll back.** In staging and production, set the older version in the deploy repository:
`deploy/scripts/set-version.sh production api 1.3.2` (or `git revert` the version commit), through a merge request
(staging: pause the `update:staging` schedule first, or it moves forward again); the pipeline applies the older images
and smoke-checks them, and the repository still says what runs. Locally, or in an emergency before the revert lands (each
Deployment keeps 5 old ReplicaSets, each with its own image and configuration):

```bash
kubectl -n product-management rollout history deployment/api
kubectl -n product-management rollout undo deployment/api
kubectl -n product-management rollout undo deployment/gateway
```

A rollback does not undo database migrations; that is why migrations must stay compatible with the previous version.
A `rollout undo` in staging or production lasts only until the next deploy job applies `main` again, so follow it
with the revert.

**Rotate a secret.** Secrets are read when a pod starts, so every rotation ends with a restart of the pods that read
it.

* *The API's own keys* (`API_SIGNING_KEY`, `API_CONSUMERS`), staging and production: change the deploy project's
  CI/CD variable and re-run its last `deploy:<env>` job. `deploy.sh` updates `api-secrets` and, as its content
  changed, runs `kubectl rollout restart deployment/api`. Locally: `FORCE=1 deploy/scripts/lib/local-secrets.sh` (a new
  signing key and consumer hashes: everyone signs in again), then `deploy/scripts/deploy.sh local`.
* *The database users' passwords* (any environment, from a machine with access to the cluster):

  ```bash
  deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator --rotate
  kubectl -n product-management rollout restart deployment/api
  ```

  The new passwords are set in PostgreSQL and in `database-credentials` at once. Open connections survive (PostgreSQL
  checks passwords only when a connection opens), but new connections of not-yet-restarted pods fail until the
  restart, so run the two together. The migrator's password is read by the restarted pods too (for their migration
  step at startup).
* *The Redis password:* a new random one, then Redis and every service that copies it:

  ```bash
  kubectl -n infra delete secret redis-auth
  deploy/scripts/infra.sh up redis     # a new redis-auth, and restarts Redis with it (the cache starts empty)
  deploy/scripts/deploy.sh local       # copies it into redis-credentials and, as it changed, restarts the API
                                       # (staging, production: re-run the deploy repository's deploy:<env> job)
  ```

  Between the Redis restart and the API's, the API cannot reach Redis: the cache and the shared rate limits fall
  back, and idempotent writes answer 503 ([chapter 12](12-from-zero.md#rotate-secrets) has the order for production).

**Scale.** The HPAs own the replica counts, so `kubectl scale` is undone within seconds. Change the range instead
(for good: in `deploy/apps/backend/hpa.yaml`, or `minReplicas`/`maxReplicas` of the gateway's HPA in
`deploy/apps/frontend/gateway.yaml`):

```bash
kubectl -n product-management patch hpa api -p '{"spec":{"minReplicas":4,"maxReplicas":8}}'
kubectl -n product-management get hpa api -w
```

Keep `maxReplicas × (10 + 1)` connections (the request pool plus the readiness check's own connection) within what
PostgreSQL can serve: 8 × 11 = 88 of its 200, which other services' databases on the same server share
([chapter 6](06-scaling-and-capacity.md#why-the-database-is-the-ceiling)). More machines for the pods: on k3s,
`deploy/scripts/add-node.sh <user@host> apps` ([chapter 12](12-from-zero.md)).

**Swagger UI** (enabled in the local and staging environments)

```bash
kubectl -n product-management port-forward deployment/api 5080:8080   # http://localhost:5080/swagger, then Authorize with a token
```

**Database users.** `deploy/scripts/lib/create-database.sh` is idempotent: run it again after a restore, or when in doubt, and it
re-applies the users' passwords from the Secret, their guard rails and the schema's ownership. The app user's rights on
the tables come from the migrations, which the API re-applies on an empty database when it starts (a
`kubectl -n product-management rollout restart deployment/api` is enough).

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
deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator   # ownership and passwords
deploy/scripts/deploy.sh local              # the migration moves the cache generations on; the HPA is back
kubectl -n product-management scale deployment/api --replicas=2
```

Restoring into a new database first (`pg_restore -d <new database>` after `CREATE DATABASE`) leaves the current one
untouched until the dump has been checked, e.g. with `PG_DATABASE=<new database> tools/load-tests/verify-data.sh`.

**Reset the data.** One service's database (the others on the server stay):

```bash
kubectl -n infra exec statefulset/postgres -- psql -U postgres -c 'DROP DATABASE product_management WITH (FORCE)'
deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator   # empty database, same users
deploy/scripts/deploy.sh local              # the migrations re-create the schema and the sample data
```

Everything in PostgreSQL, including its backups and the administrator's password:

```bash
deploy/scripts/infra.sh down postgres --delete-data
deploy/scripts/infra.sh up postgres
deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator
deploy/scripts/deploy.sh local
```

**Bulk data and load tests**

```bash
tools/load-tests/seed-bulk.sh                     # adds 850,000 products and ~5 million movements (minutes)
PRODUCTS=0 LEDGER_ROWS=20000000 tools/load-tests/seed-bulk.sh   # grows the ledger to 20 million rows (~20 minutes)
tools/load-tests/verify-data.sh                   # data invariants, all must be 0
tools/load-tests/run.sh smoke                     # any scenario: smoke, load, stress, spike, stock-consistency, rate-limit, teams, browse
```

The data scripts reach PostgreSQL with `kubectl -n infra exec -i statefulset/postgres -- psql -U postgres` and Redis
with `kubectl -n infra exec -i deployment/redis -- redis-cli` (override with `PSQL`, `REDIS_CLI`, `PG_USER`,
`PG_DATABASE`). `run.sh` raises the API's rate limits (and, for some scenarios, the gateway's) with `kubectl set env`
for the run, after saving the deployed values, and always sets those values back at the end. The autoscalers need
metrics-server, which `deploy/scripts/infra.sh up` installs where it is missing. The load tests and
`tools/check-requirements.py` are local tools, in no GitLab repository and no pipeline.

**Troubleshooting**

| Symptom | Look at |
| --- | --- |
| API pods not ready | `kubectl -n product-management describe pod -l app.kubernetes.io/name=api`, then `kubectl -n product-management logs deployment/api`; readiness includes the database (`SELECT 1`), so check it: `deploy/scripts/infra.sh status`, `kubectl -n infra logs statefulset/postgres --tail 50` |
| `deploy.sh` stops: "No Secret database-credentials" | the database was not created in this cluster: `deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator` |
| `deploy.sh` stops: "No Secret infra/redis-auth" | the shared Redis is not running: `deploy/scripts/infra.sh up redis` (or the `infra:<env>` job) |
| `deploy.sh` stops: "names no version yet for some images (newTag: unreleased)" | no version chosen for an image yet: tag a version in that app (`v1.0.0`), then `update:staging` (staging) or `set-version.sh production …` in a merge request (production) |
| `set-version.sh`: "… is not in the registry" | the version was not published (its tag pipeline failed before `publish:*`) or is mistyped: `deploy/scripts/versions.sh <image>` |
| `update:staging` does nothing | no newer plain `X.Y.Z` (release candidates are skipped; web and gateway need the same version), or the schedule is inactive |
| `deploy.sh` stops: "No Secret gitlab-registry" or "No Secret api-secrets" | the deploy project's variables: the group deploy token `gitlab-deploy-token`, `API_SIGNING_KEY` / `API_CONSUMERS` for that environment ([chapter 12](12-from-zero.md)) |
| `update:staging` / `propose:production` fails to push | the deploy project does not allow job-token pushes, or the job's user cannot push to the protected `main` | Settings → CI/CD → Job token permissions → "Allow Git push requests to the repository"; the schedule's owner (or whoever runs the job) is a Maintainer and Maintainers may push to `main` |
| The API cannot reach the database or Redis | the connection strings it runs with: `kubectl -n product-management get deployment api -o jsonpath='{.spec.template.spec.containers[0].env}'`; whether the Services answer: `kubectl -n product-management run nettest --rm -it --restart=Never --image=busybox:1.37 -- nc -zv postgres.infra.svc.cluster.local 5432` (and `redis.infra.svc.cluster.local 6379`) |
| Password authentication failed | the users' passwords no longer match `database-credentials` (e.g. after `down postgres --delete-data`): `deploy/scripts/lib/create-database.sh …` again (it sets the Secret's passwords on the users), then `kubectl -n product-management rollout restart deployment/api`. Redis `WRONGPASS`: `deploy/scripts/deploy.sh <env>` copies `redis-auth` again and restarts the API |
| A migration fails | the new api pod is in `CrashLoopBackOff` and the deploy fails at the rollout wait; the previous version's pods keep serving. `kubectl -n product-management logs deployment/api` (or `logs <pod> --previous`) shows the error after "Applying N database migrations". A pod that never starts: `kubectl -n product-management get events --sort-by=.lastTimestamp` (image pull, missing Secret) |
| A new api pod stays unready, its last log line "Another instance is migrating the database; waiting for it to finish" | another pod is applying a long migration; it starts when that one commits (at most 10 minutes) |
| `postgres-0` Pending | `kubectl -n infra describe pod postgres-0`: its volume lives on one node, so the pod can only start there (the node is down, or tainted for another role) |
| `502` on `/api` | no ready API pod: `kubectl -n product-management get endpointslices -l kubernetes.io/service-name=api` |
| `502` on `/collect` in the browser console | no log collector: `deploy/scripts/infra.sh up logs`, or deploy with `LOGS=off` |
| `ImagePullBackOff` | staging, production: the tag in `deploy/environments/<env>/kustomization.yaml` must exist in the registry, and the Secret `gitlab-registry` must hold the group deploy token (`read_registry`) |
| `smoke:*` fails | its log says which step: `/healthz` (gateway, Ingress, TLS), `/` (web), the `invalid_client` 401 (the API behind the gateway), the signed-in read (`SMOKE_CLIENT_ID` / `SMOKE_CLIENT_SECRET`, the database). Run `deploy/scripts/ci/smoke.sh <url>` from a laptop to repeat it |
| HPA shows `<unknown>` | metrics-server: `deploy/scripts/infra.sh up` installs it where missing, then `kubectl top pods` |

## Where to look

* Deploy scripts: [`deploy/scripts/deploy.sh`](../../deploy/scripts/deploy.sh),
  [`infra.sh`](../../deploy/scripts/infra.sh), [`create-database.sh`](../../deploy/scripts/lib/create-database.sh),
  [`add-node.sh`](../../deploy/scripts/add-node.sh), [`local-secrets.sh`](../../deploy/scripts/lib/local-secrets.sh),
  [`validate.sh`](../../deploy/scripts/ci/validate.sh), [`smoke.sh`](../../deploy/scripts/ci/smoke.sh); versions:
  [`versions.sh`](../../deploy/scripts/versions.sh), [`set-version.sh`](../../deploy/scripts/set-version.sh),
  [`update-versions.sh`](../../deploy/scripts/ci/update-versions.sh); Argo CD (prepared): [`deploy/argocd`](../../deploy/argocd)
* Application manifests: [`deploy/apps/backend`](../../deploy/apps/backend)
  ([`deployment.yaml`](../../deploy/apps/backend/deployment.yaml), [`service.yaml`](../../deploy/apps/backend/service.yaml)),
  [`deploy/apps/frontend`](../../deploy/apps/frontend); the environments
  [`local`](../../deploy/environments/local/kustomization.yaml),
  [`staging`](../../deploy/environments/staging/kustomization.yaml) ([`api.yaml`](../../deploy/environments/staging/api.yaml)),
  [`production`](../../deploy/environments/production/kustomization.yaml);
  [`gateway/collector.sh`](../../frontend/gateway/collector.sh)
* Shared infrastructure: [`deploy/infra/postgres.yaml`](../../deploy/infra/postgres.yaml),
  [`redis.yaml`](../../deploy/infra/redis.yaml), [`logs.yaml`](../../deploy/infra/logs.yaml),
  [`dashboards`](../../deploy/infra/dashboards)
* Migrations: [`DatabaseMigrator.cs`](../../backend/ProductManagement.Infrastructure/Persistence/DatabaseMigrator.cs),
  [`DatabaseInitializer.cs`](../../backend/ProductManagement.Infrastructure/Persistence/DatabaseInitializer.cs),
  [`Persistence/Migrations`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations)
  (the app user's rights: [`GrantAppPermissions`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations/20261010164241_GrantAppPermissions.cs)),
  the test [`MigrationsAtStartupTests.cs`](../../backend/ProductManagement.Api.IntegrationTests/Persistence/MigrationsAtStartupTests.cs)
* CI/CD: [`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml), [`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml),
  [`deploy/.gitlab-ci.yml`](../../deploy/.gitlab-ci.yml); the release notes
  [`backend/ci/release-notes.sh`](../../backend/ci/release-notes.sh) (the same in [`frontend/ci`](../../frontend/ci/release-notes.sh))
* Each repository on its own: [`backend/README.md`](../../backend/README.md), [`frontend/README.md`](../../frontend/README.md),
  [`deploy/README.md`](../../deploy/README.md)
* Decisions: [ADR 0021 deployment layout](../adr/0021-deployment-layout.md) (which supersedes most of
  [ADR 0011](../adr/0011-kubernetes-deployment.md)), [ADR 0006 PostgreSQL](../adr/0006-postgresql.md)
* Diagrams: [flows.md §9 deployment and CI/CD](../flows.md#9-deployment-and-cicd),
  [architecture.md](../architecture.md#deployment-topology)

## How to verify

```bash
deploy/scripts/deploy.sh local all             # infra, the database (first run), images, apply (the API migrates)
deploy/scripts/infra.sh status
kubectl diff -k deploy/environments/local && echo "no drift"
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

# Every environment, infra/ and argocd/ render and are valid (what the deploy pipeline's validate job runs)
docker run --rm -v "$PWD/deploy:/work:ro" -w /work alpine/k8s:1.34.11 scripts/ci/validate.sh

# The migrations at startup: every API pod logs them ("Database is up to date (0 migrations applied as pm_migrator ...)")
kubectl -n product-management rollout restart deployment/api && kubectl -n product-management rollout status deployment/api
kubectl -n product-management logs deployment/api | grep -E 'migrations|up to date' 

# The published versions (a token with read_registry in REGISTRY_USER / REGISTRY_PASSWORD, or docker login)
docker run --rm -v "$PWD/deploy:/work" -w /work -e REGISTRY_USER -e REGISTRY_PASSWORD alpine/k8s:1.34.11 scripts/versions.sh all

# The smoke check the pipeline runs after a deployment, here against the local one
deploy/scripts/ci/smoke.sh http://localhost:8080

# Without logs: /collect answers 204
deploy/scripts/infra.sh down logs && LOGS=off deploy/scripts/deploy.sh local
curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:8080/collect -H 'Content-Type: application/json' -d '{}'   # 204
```
