# 0021 — Deployment layout: Kustomize overlays, servers outside the cluster, migrations in the pipeline

**Status:** accepted (supersedes the per-project manifests and the migration Job of [0011](0011-kubernetes-deployment.md))

## Context

The first deployment ([0011](0011-kubernetes-deployment.md)) was built for one laptop:

* **One YAML per project** (`backend/deploy/kubernetes.yaml`, `frontend/deploy/kubernetes.yaml`), each with its own
  deploy script. Images were put into the manifests with `sed`, and every file declared the namespace, so
  `kubectl delete -f` on any of them deleted everything.
* **PostgreSQL and Redis ran in the cluster** (a StatefulSet with a volume, a Deployment), next to the application.
* **Migrations ran in a Job** in the cluster, with the database owner's credentials stored in the cluster.
* **One environment.** Nothing said how staging or production would differ, or where their secrets come from.

Going from there to staging and production needs: the same manifests with a few per-environment differences, no
secret values in the repository, data stores with their own backups and lifecycle, schema changes that are reviewed
and run once, and partial deployments (only the API, only the web app) that stay possible.

## Decision

**Kustomize base and overlays.** `deploy/k8s/base` holds everything that runs in the cluster, the same in every
environment; `deploy/k8s/overlays/{local,staging,production}` hold only the differences: the servers' addresses,
how the gateway is exposed (a `LoadBalancer` locally, an Ingress with a Let's Encrypt certificate elsewhere), a few
configuration values, and, for `local` only, development Secrets. `kubectl kustomize` is built into `kubectl`, so
nothing else is installed. Images are pinned by Kustomize `images` in a generated `deploy/k8s/.build/<env>`
kustomization, never by editing text.

**Parts by label.** Every object carries `app.kubernetes.io/component` (`namespaces`, `servers`, `observability`,
`api`, `web`). `deploy/deploy.sh <part>` renders the whole overlay and applies one part with
`kubectl apply -l 'app.kubernetes.io/component in (namespaces,<part>)'`, so each part still deploys on its own.
CI checks that every object has a known part (`deploy/ci/validate-manifests.sh`): an unlabelled object would
otherwise never be deployed.

**Servers outside the cluster.** PostgreSQL, Redis and the monitoring server (Grafana and Loki: logs only) run on
dedicated machines, or managed services, with their own backups, upgrades and owners. The cluster knows them only as
**ExternalName Services** (`postgres`, `redis`, and `monitoring` in the `observability` namespace for Loki), DNS
aliases set per overlay; the base points them at `set-by-overlay.invalid`, so a forgotten address fails loudly.
Locally the same servers are containers started by `deploy/infra/servers.sh`, with the same configuration files
(`postgresql.conf`, `redis.conf`) a real server uses. The cluster only runs stateless workloads and the log
collector, so it can be rebuilt from Git at any time.

**Migrations in the pipeline, with the API image.** The migrate job runs the image being released
(`dotnet ProductManagement.Api.dll migrate`) from a runner in the private network, before the rollout, never in the
cluster: the code that migrates is exactly the code being deployed, it runs once per release, and a failure stops the
pipeline before any pod changes. Every migration must be **backward compatible** with the version still running
(expand/contract): the old pods serve traffic while it runs and next to the new pods during the rollout. Renaming a
column is three releases (add and write both, switch reads, drop the old one). The pipeline also publishes the
idempotent SQL script of all migrations (`dotnet ef migrations script --idempotent`) for review.

**Least-privilege database users** (`deploy/infra/postgres/roles.sql`): `pm_migrator` owns the schema and is used
only by the migrate job; `pm_app` (the API) may read and write rows and use the sequence, with a 30 s statement
timeout, but cannot change or drop anything. A bug or an injected query in the API cannot alter the schema, and the
cluster holds no credential that can.

**Secrets from CI/CD variables.** Staging and production overlays contain no Secret (CI fails if one appears). The
deploy job creates the API's four Secrets from protected, environment-scoped GitLab variables
(`deploy/ci/create-secrets.sh`): passwords as masked variables, the signing key and the consumers file as File
variables. When a Secret's content changes, the job restarts the API so the pods read it.

**Configuration from `configMapGenerator`.** The API's settings are `deploy/k8s/base/api/config.env`, merged with
per-overlay literals. Kustomize names the ConfigMap after a hash of its content, so a configuration change produces a
new ConfigMap name, changes the Deployment's pod template and rolls the pods like a code change; old ReplicaSets keep
pointing at the old ConfigMap, so `kubectl rollout undo` restores configuration too.

**`api-headless` is the API's only Service.** The gateway balances per request over the pod addresses the headless
Service returns ([0012](0012-gateway.md)); a ClusterIP Service in front of the API would only invite clients to bypass
that.

**GitLab pipeline** (`.gitlab-ci.yml`): test → build → scan → staging (migrate, deploy) → verify (the assessment
check and Playwright against staging) → production (manual migrate, then deploy). It reaches the clusters through
the GitLab agent for Kubernetes, so no cluster API is exposed to the runners. GitHub Actions keeps the same checks
plus a full deployment to MicroK8s on every change.

## Alternatives considered

* **Helm.** A chart is the right packaging for software that others install with many options. Here there is one
  consumer and three environments that differ in a handful of values; templates would add a language to read and a
  release store to manage, for differences that Kustomize expresses as plain patches. Nothing prevents wrapping the
  base in a chart later.
* **One file per application** (the previous layout), with `sed` for images and one script per project. Simple, but
  every environment difference becomes another file or another substitution, and nothing is checked before
  `kubectl apply`.
* **GitOps with Argo CD or Flux.** The natural next step, and the layout is ready for it: the overlays are what an
  Argo CD `Application` (or a Flux `Kustomization`) would point at. The pipeline would keep test, build, scan and
  migrate, and instead of `kubectl apply` it would commit the new image references to the overlay (or let an image
  updater do it); the controller in the cluster applies and keeps applying it, with drift detection, pruning and a
  history in Git. It was not done now because it adds a controller to run and secure, and the assessment's
  environments are deployed from one pipeline anyway.
* **Databases in the cluster** (StatefulSets, or an operator such as CloudNativePG for PostgreSQL). An operator gives
  failover, backups to object storage and point-in-time recovery inside Kubernetes, and is a good choice for a team
  that runs Kubernetes well. Dedicated servers (or managed services) were chosen because they keep the data's
  lifecycle separate from the cluster's: a cluster rebuild or upgrade never touches the data, and the operations
  (backups, restores, tuning) are the standard PostgreSQL ones. The ExternalName indirection means switching to an
  in-cluster operator later changes one name per overlay.
* **Migrations at startup or in a Job.** Startup migrations race between replicas and need schema rights in every
  pod; a Job needs the owner's credentials in the cluster and separates the migration's failure from the pipeline.

## Consequences

* Staging and production are deployed by the same script as a laptop, with the same manifests; their differences
  are visible in one small `kustomization.yaml` each.
* **`kubectl apply -l` does not prune.** Deleting an object from the manifests does not delete it from the cluster:
  remove it by hand (`kubectl delete <kind> <name>`) in each environment, or move to GitOps, which prunes.
  `kubectl apply --prune` was not used: with label selectors it is easy to delete more than intended.
* Partial deploys apply a part of a rendered whole: objects shared between parts must belong to exactly one part
  (the namespaces are their own part and are applied with every part).
* Migrations must be written expand/contract; a release cannot assume the schema change and the code change land at
  the same instant. Rollbacks never undo migrations: a bad migration is fixed forward.
* The migrate job needs a runner inside the private network, and the database must accept connections from it.
* Secret rotation is "change the variable, re-run the deploy job" (and, for database passwords, `roles.sql` on the
  server first). A secret manager (External Secrets, Vault) is the next step for audit and automatic rotation.
* NetworkPolicies (only the gateway reaches the API and the web app) are in the base for every environment, but
  only enforced by network plugins that implement them (k3s, Calico, Cilium); Docker Desktop accepts and ignores them.
* ExternalName Services are DNS aliases: the API's TLS certificate checks see the alias, so the database
  certificate must also name `postgres.product-management.svc.cluster.local` (or the API must use the server's own
  name) before `SSL Mode=VerifyFull` can be used ([guide chapter 12](../guide/12-from-zero.md)).

## Update (2026-10-10)

The decisions above still hold (Kustomize overlays, servers outside the cluster, migrations in the pipeline with the
API image, least-privilege users, secrets from CI/CD variables). How they are laid out changed: the paths, part
labels, ExternalName Services, ConfigMap and Secrets described above are history.

**Three self-contained parts, each a future GitLab repository.** The single `deploy/` folder and its one script with
parts (`deploy/deploy.sh infra|observability|api|web`, selected by the `app.kubernetes.io/component` label) are
replaced by three folders that each hold their code or configuration, deploy script, manifests and pipeline:

* **`infra/`**: the servers outside the cluster and the cluster's log collector. `infra/infra.sh up|down|status
  [database] [redis] [logs]`; `infra/.gitlab-ci.yml` validates every change and deploys only through manual jobs,
  per environment and component, on a shell runner on each server.
* **`backend/`**: the API. `backend/deploy/deploy.sh` (local secrets, image, migrations, rollout),
  `backend/deploy/k8s/{base,overlays/<env>}`, `backend/deploy/ci/` and `backend/.gitlab-ci.yml`. The load tests and
  the requirements check moved here too (`backend/load-tests/`, `backend/scripts/`).
* **`frontend/`**: the web app and the gateway. `frontend/deploy/deploy.sh`, `frontend/deploy/k8s/{base,overlays/<env>}`,
  `frontend/deploy/ci/validate-manifests.sh` and `frontend/.gitlab-ci.yml`.

Each part is deployed as a whole by its own script (`kubectl apply` of its rendered overlay), so the component labels
and `kubectl apply -l` are gone. The parts meet only at addresses: the API's connection strings (backend → infra), the
gateway's `API_UPSTREAM` and `JWKS_URL` (frontend → backend) and `COLLECTOR_UPSTREAM` (frontend → infra, optional).
Both the backend and the frontend apply the namespace `product-management`; whichever deploys first creates it.

**Configuration as env keys in the Deployment.** The generated ConfigMap (`config.env` → `api-config-<hash>`) is
removed. Every setting is a plain `env` entry in `backend/deploy/k8s/base/deployment.yaml`; each overlay's `api.yaml`
is a strategic-merge patch with only what differs, and always the two connection strings. The values are in the pod
template, so a change still rolls the pods and `kubectl rollout undo` still restores configuration with code, with
one file less and no name hashing to explain. The gateway works the same way (`gateway.yaml` in the base and in each
overlay).

**One Secret, `api-secrets`**, instead of `api-database`, `api-redis`, `api-auth` and `api-consumers`. Its keys are
`database-password` (→ `PGPASSWORD`, which Npgsql uses when the connection string has no password), `redis-password`
(→ `Redis__Password`, which the API sets on the Redis connection, over any password in the connection string),
`signing-key` (→ `Authentication__SigningKey`) and `consumers.json` (mounted as `/app/config/consumers.json`). The
connection strings hold no password, so they sit in plain YAML in the overlay and can be read and reviewed like any
other setting. Locally Kustomize's `secretGenerator` builds `api-secrets-<hash>` from the files that
`backend/deploy/local-secrets.sh` writes (including `generated/passwords.env`); in staging and production
`backend/deploy/ci/create-secrets.sh` creates it from `PM_APP_PASSWORD`, `REDIS_PASSWORD`, `API_SIGNING_KEY` and
`API_CONSUMERS`. The migration step gets its password the same way (`PGPASSWORD` from `PM_MIGRATOR_PASSWORD`).

**Docker Compose per infra component, each on or off.** Locally (and on the servers, through the infra pipeline) each
component is its own Compose file and project: `database.yaml` (PostgreSQL and a one-shot `roles` service that
applies `roles.sql` on every `up`), `redis.yaml`, `logs.yaml` (Loki and Grafana under the profile `storage`, and the
server agent). The data volumes are created by `infra.sh` and declared `external`, so `docker compose down -v` cannot
delete them. **Logs are optional:** without the `logs` component the application works the same, the pods log to
stdout, and the frontend is deployed with `LOGS=off` so the gateway answers `/collect` with 204 (its new
`collector.sh` writes either the proxy or the `return 204`). With `LOKI_URL` (and `LOKI_USERNAME` / `LOKI_PASSWORD`)
the server agent and the cluster's collector ship to another Loki, such as Grafana Cloud, and no local Loki or Grafana
runs.

**ExternalName Services removed.** `postgres`, `redis` (in `product-management`) and `monitoring` (in
`observability`) were DNS aliases set per overlay. The connection strings and `LOKI_URL` now name the servers
directly (`host.docker.internal` locally, the private DNS names elsewhere). One indirection less, and the TLS
certificates only need the servers' own names, not the in-cluster aliases.

**Service `api-headless` renamed to `api`.** Still headless (`clusterIP: None`), still the API's only Service; the
in-cluster name is now `api.product-management.svc.cluster.local:8080`.

### Consequences of the update

* Each part can be cloned, run and deployed on its own, with its own README, pipeline, registry and variables. The
  price is some duplication: both deploy scripts have the same image helpers (tag, publish, pin), the namespace is
  declared twice, and the three database and Redis passwords must be the same in the infra and the backend projects
  (group-level variables in GitLab).
* Deploy order matters across repositories: infra, then backend, then frontend. Nothing enforces it; a frontend
  deployed before the API answers `/api` with 502 until the API is there.
* Without the ExternalName indirection, moving a server (or switching to an in-cluster operator) means editing the
  connection strings in the backend's overlays and redeploying the API, instead of changing one alias.
* With no `kubectl apply -l`, a deploy applies everything in its overlay; it still does not prune (an object removed
  from the manifests must be deleted by hand), and locally old `api-secrets-<hash>` Secrets accumulate.
* A setting changed in the Deployment is visible in `kubectl get deployment api -o yaml` and in Git, but a
  `kubectl set env` (as the load tests do for the rate limits, restoring them afterwards) is undone by the next deploy.
* The GitHub Actions workflow still tests and deploys all three parts together, in order, as the end-to-end check.
