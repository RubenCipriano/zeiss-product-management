# 0021 — Deployment layout: Kustomize overlays, servers outside the cluster, migrations in the pipeline

**Status:** accepted (supersedes the per-project manifests and the migration Job of [0011](0011-kubernetes-deployment.md)),
amended three times. **Current state:** where things run is
[Update (2026-10-10): the infrastructure moves into Kubernetes](#update-2026-10-10-the-infrastructure-moves-into-kubernetes)
(PostgreSQL, Redis and the logs in the cluster, in the shared namespace `infra`); the layout and the CI/CD flow are
[Revision (2026-10-10): GitOps with a deploy repository](#revision-2026-10-10-gitops-with-a-deploy-repository)
(every manifest and environment in the deploy repository, whose pipeline is the only one that applies). The servers
outside the cluster, the Docker Compose files, the private runner for migrations, the password variables and the
per-repository deploy scripts and manifests described below are history.

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

*The paths in this section and in the first update are those of their time (a root `deploy/` folder, then
`backend/deploy/` and `frontend/deploy/`); today's are in the [revision](#revision-2026-10-10-gitops-with-a-deploy-repository).*

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
the GitLab agent for Kubernetes, so no cluster API is exposed to the runners.

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
labels, ExternalName Services, ConfigMap and Secrets described above are history. (The three-part layout below was in
turn replaced by the deploy repository: [revision](#revision-2026-10-10-gitops-with-a-deploy-repository).)

**Three self-contained parts, each its own GitLab repository**
([backend](https://gitlab.com/zeiss/backend), [frontend](https://gitlab.com/zeiss/frontend) and
[deploy](https://gitlab.com/zeiss/deploy), the `infra/` folder). The single `deploy/` folder and its one script with
parts (`deploy/deploy.sh infra|observability|api|web`, selected by the `app.kubernetes.io/component` label) are
replaced by three folders that each hold their code or configuration, deploy script, manifests and pipeline. CI/CD
is GitLab only: each part's `.gitlab-ci.yml`, and no workflow that builds or deploys the three together.

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
  price is some duplication: both deploy scripts have the same image helpers (tag, pin), the namespace is
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
* No pipeline deploys the three parts together: each one verifies its own part against staging (the requirements
  check for the backend, Playwright for the frontend), which assumes the other parts are already there.

## Update (2026-10-10): the infrastructure moves into Kubernetes

This update supersedes "Servers outside the cluster", "Migrations in the pipeline" (where they run), "Least-privilege
database users" (where they are made) and "Secrets from CI/CD variables" (for the passwords) above, and the Docker
Compose part of the first update. Kustomize overlays, the three parts, configuration as env keys and the headless
`api` Service are unchanged.

### Context

After the first update the shared infrastructure was a set of Docker Compose projects: containers on a laptop, and on
each server a shell runner that ran the same files. The cluster reached them through `host.docker.internal` or
private DNS names; the migrate jobs needed a self-hosted runner inside the private network, the only place that could
reach the database; the three database and Redis passwords were pipeline variables that the infra and the backend
projects had to keep identical; the logs needed two collectors (a server agent for the containers, Alloy for the
pods). There were two ways to run things, and only one of them was Kubernetes.

### Decision

**Everything runs in Kubernetes**, in one shared namespace, `infra`, for every service on the cluster (not only
product-management), managed by the infra repository (since the revision below: the deploy repository's `infra/`
and `scripts/`):

* `postgres.yaml`: PostgreSQL 18 as a StatefulSet (one replica, its data on a PersistentVolumeClaim, the settings
  inline in a ConfigMap) behind the Service `postgres.infra.svc.cluster.local`, and a CronJob that writes a nightly
  `pg_dump` of every database to a second volume.
* `redis.yaml`: Redis 8 as a Deployment without a volume (a pure cache), password protected.
* `logs.yaml`: Loki, Grafana (everything provisioned from ConfigMaps) and one Alloy that reads every pod's log in
  every namespace, PostgreSQL's and Redis' included, and receives the browser's errors.
* `infra.sh up|down|status [postgres] [redis] [logs] [--delete-data]` applies them; `down` keeps the volumes and the
  passwords unless `--delete-data`. It also installs metrics-server where the cluster has none (the former
  `cluster-addons.sh`).

**One PostgreSQL for the platform, one database per service.** `create-database.sh <namespace> <database> [app-user]
[migrator-user] [--rotate]` creates a service's database and its two users with random passwords, and writes them to
the Secret `database-credentials` in the service's namespace. What the app user may do on the tables is the service's
own business: for the API, the EF Core migration `GrantAppPermissions` (run as the schema owner) replaces `roles.sql`.

**Passwords are generated in the cluster and never leave it.** `infra.sh` creates the administrator's and Redis'
passwords (Secrets `postgres-admin`, `redis-auth`), `create-database.sh` the users'. `deploy.sh` (then the backend's,
now `deploy/scripts/deploy.sh`) copies Redis' password into the service's namespace (`redis-credentials`), since a pod reads only its namespace's Secrets. No
pipeline variable holds a database or Redis password any more; `api-secrets` holds only the API's signing key and
consumers.

**Migrations still run with the release's image, once, before the rollout**, but as a one-off pod in the cluster
(`kubectl run --rm --attach`): the database is reachable only there. The pod streams its output, returns its exit code
and is deleted when it ends; no Job manifest, no port-forward, and no runner inside the private network: every job
runs on gitlab.com's shared runners and reaches the cluster through the GitLab agent (or a kubeconfig).

**Dedicated nodes instead of separate servers.** In production the machines become k3s nodes; `add-node.sh <user@host>
database|cache|apps` joins one with the label `infra/role=<role>` and, for `database` and `cache`, the taint
`infra/role=<role>:NoSchedule`. PostgreSQL and Redis prefer their node and tolerate its taint, so they get it to
themselves; without such a node (a laptop, a single k3s server) they run anywhere.

**Docker Compose is dropped**, with everything that existed only for it: the Compose files, the server agent, the
`observability` namespace, the private runner and the shell runners on the servers.

### Why

* **One way to run everything.** The same manifests and scripts run on Docker Desktop, on a single k3s machine and on
  a production cluster; there is no second toolchain for the stateful half of the system.
* **The platform is shared by services.** A new service gets a database with one command and its logs without doing
  anything; the address of PostgreSQL, Redis and Loki is the same cluster name in every environment.
* **Kubernetes does what the servers needed by hand:** restarts, health probes, scheduling onto the right node,
  passing the passwords as Secrets, one log collector for everything.
* **Fewer secrets to carry.** No password crosses GitLab, and no runner needs network access to the database.

### Trade-offs

* **Stateful workloads on Kubernetes need care.** A volume on a node's disk (`local-path`, `hostpath`) is bound to
  that node, and the pod with it: the database node must exist before PostgreSQL first starts, a lost node means a
  restore, and draining that node stops the database.
* **Backups are dumps on the same node** until production ships them elsewhere (object storage or volume snapshots).
  They restore to the moment of the dump; there is no WAL archiving, no point-in-time recovery.
* **No high availability.** One PostgreSQL pod, one Redis pod. A standby with failover and continuous backups is an
  operator's job (CloudNativePG for PostgreSQL), the next step ([0006](0006-postgresql.md)). The earlier argument for
  servers outside the cluster, that a cluster rebuild never touches the data, no longer holds: the data's lifecycle
  is now the cluster's, and etcd holds the only copy of the generated passwords, so etcd snapshots must leave the
  nodes too.
* **The migrator's password is in the cluster** (the Secret `database-credentials` in the service's namespace, read
  only by the migration pod). The API pods still read only `pm_app`'s password, which cannot change the schema.
* **Rotation is an operator's command** (`create-database.sh --rotate`, a new `redis-auth`) followed by restarting the
  pods that read the Secret, not "change a variable and re-run a job".

## Revision (2026-10-10): GitOps with a deploy repository

This revision supersedes the "three self-contained parts" of the first update (each with its own deploy script,
manifests and deploying pipeline) and the "GitOps with Argo CD or Flux" alternative's "not now". Kustomize, the shared
infrastructure in the cluster, configuration as env keys, the headless `api` Service, least-privilege users and
migrations with the release's image, once, before the rollout, are unchanged.

### Context

After the updates, an environment was spread over three repositories: the API's manifests and overlays in
`backend/deploy/`, the web app's and the gateway's in `frontend/deploy/`, the shared infrastructure in `infra/`. Each
app pipeline applied its own part to the clusters, so what ran in staging was the sum of the last successful deploy
jobs of three pipelines, and no file said it. A rollback was a re-deploy per project; the namespace was declared
twice; each project had its own pull Secret; and the frontend pipeline needed cluster rights only to apply two
Deployments.

### Decision

**One deploy repository describes every environment** (`deploy/`, [gitlab.com/zeiss/deploy](https://gitlab.com/zeiss/deploy);
the former `infra/` folder, renamed):

* `apps/backend/`, `apps/frontend/`: the applications' manifests, the same in every environment (the former bases;
  the namespace only in `apps/backend`).
* `environments/{local,staging,production}/`: one `kustomization.yaml` per environment for both apps, with the
  per-environment patches (`api.yaml`, `gateway.yaml`, and for staging and production `registry.yaml`,
  `ingress.yaml`) and, in staging and production, the deployed image tags (`images:` with `newTag`).
* `infra/`: `postgres.yaml`, `redis.yaml`, `logs.yaml`, `dashboards/`.
* `scripts/`: `deploy.sh <local|staging|production> [infra|apps|all]` (replaces both apps' deploy scripts, their
  `ci/deploy-environment.sh` and `create-secrets.sh`), `infra.sh`, `create-database.sh`, `add-node.sh`,
  `local-secrets.sh`, `validate.sh` (every environment and `infra/`, replacing both `validate-manifests.sh`) and
  `smoke.sh`.

**The app pipelines never apply.** backend and frontend test, build and scan; on `main` they *release*: their
`ci/release.sh` commits the new tag to `environments/staging/kustomization.yaml` on the deploy repository's `main`
("backend: api <sha>", "frontend: web, gateway <sha>"), and for production pushes a branch and opens a merge request
("Release api <sha> to production"). The backend runs `ci/migrate.sh` (the same one-off migration pod) before each
release commit, staging automatically, production manually.

**The deploy pipeline is the only one that changes the clusters.** It validates every merge request and commit; on
`main` it applies staging (`deploy:staging` → `scripts/deploy.sh staging apps`) when a commit changes
`environments/staging`, `apps/`, `infra/` or the script, and smoke-checks it through the public URL (`smoke:staging`);
production the same after a release merge request is merged, started by hand (`deploy:production`). The shared
infrastructure is applied only by manual `infra:<env>` jobs. `deploy.sh` creates one pull Secret, `gitlab-registry`,
from a group deploy token (`gitlab-deploy-token`, `read_registry` on the zeiss group) that reads both registries, and
`api-secrets` from the deploy project's `API_SIGNING_KEY` and `API_CONSUMERS`; it refuses an environment whose images
are still `newTag: unreleased`.

**Locally** `scripts/deploy.sh local` builds the three images from the sibling checkouts `../backend` and
`../frontend`, records their tags in `environments/local/generated/build` (not committed), migrates and applies, so
`kubectl diff -k environments/local` shows no drift after a deploy. The load tests and the requirements check are
local tools (`tools/`), in no repository; the end-to-end tests and the requirements check are no longer pipeline
jobs (they are run by hand against an environment); the smoke check replaces them after each deployment.

### Why

* **One place that describes every environment.** `environments/<env>` says exactly which images and settings run
  there; a reviewer sees an environment, not three halves of it.
* **History is the deployment history.** One commit per release: `git log environments/staging` is staging's
  deployment log, with author and pipeline link.
* **Rollback is `git revert`** of the release commit (or merge); the pipeline applies the previous tags, and the
  repository keeps saying what runs.
* **Promotion is a merge request.** Production gets a tag staging already runs; reviewing and merging the merge request
  is the release decision, with GitLab's approvals.
* **Ready for Argo CD.** An `Application` per environment pointing at `environments/<env>` (automated sync for
  staging, manual for production) replaces the deploy and smoke jobs; the app pipelines do not change.
* **Fewer cluster rights.** The frontend pipeline needs none; the backend only for its migrations.

### Trade-offs

* **A cross-repository token.** The release jobs push to the deploy repository with `DEPLOY_REPO_TOKEN`, a project
  access token of `zeiss/deploy` (Maintainer, `write_repository`), masked and protected in backend and frontend. It
  can push to deploy's protected `main`, so it is a credential to guard, expire and rotate.
* **Migrations still run from the backend pipeline**, before the release commit, not from the deploy repository: the
  migration needs the image being released, and the backend keeps cluster access for it. Production's migration
  (`migrate:production`, manual) runs just before `release:production` opens its merge request, so before anyone
  merges it; expand/contract makes that safe, and a
  revert never undoes the schema.
* **The local build reads sibling checkouts.** `deploy.sh local` expects `../backend` and `../frontend` next to the
  deploy repository (or `BACKEND_DIR`, `FRONTEND_DIR`).
* **Two releases at the same moment** race for deploy's `main`; `ci/release.sh` starts again from the new `main` (up to
  five times).
* **A production release is two steps** (merge, then start `deploy:production`), and a fresh environment refuses to
  deploy until both apps have released once.
* **Thinner automated checks after a deployment:** the smoke check (gateway, web app, the API through the gateway,
  optionally a signed-in read) instead of the requirements check and Playwright, which now run by hand.
* Still no pruning: `kubectl apply` does not delete objects removed from the manifests (Argo CD would).
