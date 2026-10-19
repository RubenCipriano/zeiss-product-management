# 0021 — Deployment layout: Kustomize overlays, servers outside the cluster, migrations in the pipeline

**Status:** accepted (supersedes the per-project manifests and the migration Job of [0011](0011-kubernetes-deployment.md)),
amended six times. **Current state:** where things run is
[Update (2026-10-10): the infrastructure moves into Kubernetes](#update-2026-10-10-the-infrastructure-moves-into-kubernetes)
(PostgreSQL, Redis and the logs in the cluster, in the shared namespace `infra`); the layout is
[Revision (2026-10-10): GitOps with a deploy repository](#revision-2026-10-10-gitops-with-a-deploy-repository)
(every manifest and environment in `deploy/`, whose workflow is the only one that applies); the release
flow is [Revision (2026-10-10): pull model with SemVer releases](#revision-2026-10-10-pull-model-with-semver-releases)
(the app pipelines only publish versions; `deploy/` pulls them in); the migrations are
[Revision (2026-10-11): the API applies its migrations at startup](#revision-2026-10-11-the-api-applies-its-migrations-at-startup)
(no migration job, pod or hook any more); where the code lives and what runs the CI/CD is
[Revision (2026-10-19): one GitHub repository, GitHub Actions](#revision-2026-10-19-one-github-repository-github-actions)
(the three GitLab projects became folders of one GitHub repository; the pipelines became workflows). The servers
outside the cluster, the Docker Compose files, the private runner for migrations, the migration pod (`migrate.sh`, the
`migrate` command, the PreSync hook), the password variables, the per-repository deploy scripts and manifests, and
every GitLab detail (projects, `.gitlab-ci.yml` jobs, CI/CD variables, deploy tokens, the job token, the pipeline
schedule, the pull Secret `gitlab-registry`) described below are history.

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
  (Revisited on 2026-10-11: the API now migrates at startup, with an advisory lock against the race and the schema
  rights confined to a startup step; see the last revision.)

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
  `smoke.sh`. Since 2026-10-11 grouped by who runs them, with a menu (`menu.sh`) in front: `scripts/` what a person
  runs, `scripts/ci/` what only the pipeline runs (`validate.sh`, `update-versions.sh`, `smoke.sh`), `scripts/lib/`
  what the others call (`local-secrets.sh`, `create-database.sh`).

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
`kubectl diff -k environments/local` shows no drift after a deploy. Since 2026-10-11, without those checkouts (a
server or a demo with only the deploy repository) it runs a published version from the registry instead
(`--version X.Y.Z`, with a read_registry deploy token and the pull Secret `gitlab-registry`, written into the same
generated component), so the committed overlay serves both. The load tests and the requirements check are
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

## Revision (2026-10-10): pull model with SemVer releases

This revision replaces how a new image reaches an environment in the previous revision ("The app pipelines never
apply": their `ci/release.sh` committing tags into the deploy repository, the backend's `ci/migrate.sh`) and its
first two trade-offs. The deploy repository's layout, its pipeline as the only one that applies, Kustomize, the
shared infrastructure and the migration pod are unchanged.

### Context

In the previous revision releases were **pushed**: on every pipeline on `main`, backend and frontend committed their
new image tag (the commit hash) into `environments/staging` of the deploy repository, and opened merge requests for
production. That worked, but:

* **The app repositories needed write access to the deploy repository** (`DEPLOY_REPO_TOKEN`, Maintainer, able to push
  to its protected `main`), and the backend needed **cluster access** for its migrate jobs. A compromised or mistaken
  app pipeline could change an environment; the credential lived in two projects.
* **Every commit on `main` went to staging**, identified only by a hash; nothing said what changed between two
  deployments, and there was no named version to go back to.
* **Migrations ran in the app pipeline** before the release commit, so production's schema moved when someone started
  `migrate:production`, not when production actually took the version.
* It is the opposite of how Argo CD Image Updater works (it watches the registry and writes back to git), so moving to
  Argo CD would have changed the flow, not only the tool.

### Decision

**App pipelines only build and publish.** backend and frontend: test → build (`<image>:<short sha>`) → scan on every
merge request and `main`. A git tag `v<MAJOR>.<MINOR>.<PATCH>` (optionally `-rc.<N>`), created by a maintainer
(protected tags `v*`), runs the same pipeline (reusing `main`'s image of that commit), then `publish:*` adds the tag
`<MAJOR>.<MINOR>.<PATCH>` (SemVer, no `v`) to the tested and scanned image with `crane tag` (same digest), and
`release` creates a GitLab Release (`release:` keyword, `release-cli` image) with the image references and digests
and the commits since the previous version (`ci/release-notes.sh`). They hold no credential of the deploy repository or
of any cluster. Each app README says which change raises MAJOR, MINOR or PATCH.

**The deploy repository chooses versions.** `environments/<env>/kustomization.yaml` names a SemVer version per image
(`unreleased` until the first one, which `deploy.sh` refuses). `scripts/versions.sh` lists the published versions from
the registry's v2 API (curl, with the group deploy token or a `docker login`); `scripts/set-version.sh <env>
<image|frontend|all> <version|latest>` checks the version exists and edits `newTag` (upgrade and downgrade alike; `latest`
excludes pre-releases unless `--pre` and never moves backwards; web and gateway move together). Staging is updated by
the scheduled job `update:staging` (`scripts/ci/update-versions.sh staging`: the newest `X.Y.Z`, committed to `main` as
"staging: api 1.4.0, ..." with the job's own token (`CI_JOB_TOKEN`, allowed to push to its own project; GitLab Free has no project access tokens), the same
thing Image Updater's git write-back does). Production changes only by merge request (`set-version.sh production ...`,
or the manual `propose:production`, which proposes staging's versions).

**Migrations move to the deploy repository.** `scripts/migrate.sh <env>` reads the API image from the rendered
environment and runs it as the one-off migration pod; `deploy.sh` calls it for every environment (local included:
one migration path) right before `kubectl apply`, and a failed migration stops the deploy.

**Argo CD prepared, not installed.** `deploy/argocd/`: one Application per environment (staging automated with prune
and selfHeal, production manual), Argo CD Image Updater annotations on the staging Application (semver, only `X.Y.Z`,
git write-back of the kustomization to `main`) with the `ImageUpdater` object that Image Updater 1.x requires, and the
migration as a PreSync hook Job in a Kustomize Component the environments include on the day of the switch.
`validate.sh` validates them against the CRDs of the pinned versions on every change; the deploy README has the
switch procedure.

### Consequences

* **No cross-repository credential.** Backend and frontend have no CI/CD variable at all; the only token that can push
  to the deploy repository is the deploy repository's own job token, used by its own jobs. Only the deploy
  project reaches a cluster.
* **Environments run named versions** with release notes; `git log environments/production` reads "production: api
  1.4.0, web 2.1.0, gateway 2.1.0". The image of a version is exactly the one tested on `main` (same digest), and tag
  immutability keeps it so.
* **Releasing is a deliberate act** (a tag), not every merge. Commit builds still exist for testing, and an environment
  can be pinned to a release candidate by hand.
* **The schema moves with the environment**: production's migration runs when production deploys that version, not
  earlier. Expand/contract still makes it safe for the old pods.
* **Rollback is the same command** with an older version. In staging the schedule moves forward again, so a rollback
  there means pausing the schedule (or, better, releasing a fix).
* **Staging lags a release by up to the schedule interval** (15 minutes), plus the deploy.
* **Two writers to `main`** (people, `update:staging`); the job starts again from the new `main` when it moved. With
  Argo CD, Image Updater replaces `update:staging`, never both.
* **Argo CD readiness.** The switch changes who does each step, not the flow: Argo CD applies instead of `deploy:*`,
  its PreSync hook migrates instead of `migrate.sh`, Image Updater writes the versions instead of `update:staging`;
  production stays a merge request plus a manual sync. Releasing in the app repositories does not change at all.

## Revision (2026-10-11): the API applies its migrations at startup

**Status:** accepted. Replaces the migration step of every earlier section (the pipeline's migrate jobs, then the
one-off pod of `deploy/scripts/migrate.sh` and the prepared Argo CD PreSync hook).

### Context

Migrations had become a step of their own: the API image run with the command `migrate` as a one-off pod
(`kubectl run --rm --attach`), started by `deploy/scripts/migrate.sh` before every `kubectl apply`, with a PreSync hook
Job prepared for Argo CD. It worked, but it was a second path to keep in step with the first: a script that had to
read the image tag out of the rendered environment, a pod spec duplicated in shell and in a hook manifest, a separate
Redis and registry wiring, and a step that anything else that runs the image (`docker run`, Compose, Argo CD without
the hook) had to remember. The request was simple: "the migrations should be done in the API itself instead of a
pod". The two classic objections to startup migrations were the ones recorded under "Alternatives considered": replicas
race, and every pod needs schema rights.

### Decision

**The API applies its own EF Core migrations when it starts, before it serves**
(`ProductManagement.Infrastructure/Persistence/DatabaseMigrator.cs`, called from `Program.cs` before
`app.RunAsync()`), whenever `Database:ApplyMigrationsOnStartup` is `true`: the default in `appsettings.json`, and set
explicitly in `deploy/apps/backend/deployment.yaml`. One path for every environment and every way of running the image.

* **The migrator's credentials, for the migration only.** `Database:MigratorUsername` and `Database:MigratorPassword`
  (in Kubernetes from the Secret `database-credentials`, keys `migrator-user` and `migrator-password`) replace the user
  and password of the API's connection string for a short-lived context on unpooled connections, closed before the API
  serves. Requests keep using `pm_app`, which cannot change the schema. Without a migrator configured (local
  development, the tests) the connection string is used as it is.
* **One replica at a time: a PostgreSQL advisory lock.** The first pod takes `pg_try_advisory_lock(<key>)` on a
  connection of its own and holds it for the whole run (migrations and the seeding hook); the others ask again every
  2 s, then find the schema up to date. If the holder dies, its session ends and PostgreSQL releases the lock.
* **Evidence for our own lock.** EF Core 9+ takes a migration lock, but Npgsql 10.0.3 implements it as
  `LOCK TABLE "__ef_migrations_history" IN ACCESS EXCLUSIVE MODE` with `LockReleaseBehavior.Transaction`
  (`NpgsqlHistoryRepository`), and EF Core's `Migrator` commits each migration's transaction separately (and commits
  before a `suppressTransaction` statement such as `CREATE INDEX CONCURRENTLY`), so the lock is released between
  migrations while the list of pending migrations was computed once at the start. Two instances started together on an
  empty database failed with a duplicate key in the history (one applied `GrantAppPermissions` again). With the
  advisory lock, the integration test `MigrationsAtStartupTests` starts two instances at once on an empty database:
  both come up and the schema is applied once.
* **Waiting replicas poll.** Blocking in `pg_advisory_lock` deadlocked against `CREATE INDEX CONCURRENTLY`: the blocked
  statement holds a snapshot, and the index build waits for every older snapshot to end (measured: the test hung).
* **Serving waits for the schema.** The HTTP server starts after the migrations, so neither `/health/live` nor
  `/health/ready` answers before. The startup probe allows 10 minutes (`periodSeconds: 5`, `failureThreshold: 120`)
  for a long migration or the wait for another pod's; the migration's statements and the lock wait are bounded by the
  same 10 minutes. `progressDeadlineSeconds: 900` and a 15-minute rollout wait in `deploy.sh` keep a long migration
  from being reported as a stuck rollout.
* **A failed migration stops the rollout, not the service.** The new pod exits and restarts (`CrashLoopBackOff`), never
  becomes ready, and `maxUnavailable: 0` keeps every old pod serving; `deploy.sh` fails at the rollout wait.
* **Sample data per environment.** `Database__SeedSampleData` in `environments/<env>/api.yaml`: `true` locally and in
  staging, `false` in production (what `migrate.sh`'s `SEED_SAMPLE_DATA` defaulted to).
* **Removed:** `deploy/scripts/migrate.sh`, `deploy/argocd/hooks/`, the API's `migrate` command, the `migrate-<version>`
  pod and its rules in the deploy pipeline. **Kept:** `create-database.sh` before the apply (the database and its users
  must exist before the API can migrate), and the backend's `migrations:sql` job (the idempotent SQL, for review in the
  merge request).

### Why

* **One path, nothing to forget.** Whatever starts the image (the pipeline, `deploy.sh local`, Argo CD, `docker run`,
  Compose) gets the schema of exactly that code. Argo CD needs no hook.
* **The two objections are met.** Replicas take turns on the advisory lock instead of racing; the requests never have
  schema rights, only the startup step does, on connections of its own.
* **Operational behaviour stays the same where it matters**: the old version keeps serving until the new one is ready
  on the new schema, a failure is visible in the rollout, and expand/contract still governs every migration.

### Trade-offs

* **The pods hold the migrator's password.** Before, only the migration pod did. A request (or an injected query)
  still cannot use it, because requests never use that connection; code execution inside a pod could. Mitigations: the
  container runs non-root with a read-only filesystem and no service account token, and `pm_migrator` owns only this
  service's schema. A team that wants the strict separation back can run the same image with
  `Database__ApplyMigrationsOnStartup=false` in the Deployment and `true` in a Job (no code change).
* **A long migration delays the first new pod** (up to the startup probe's 10 minutes) instead of the deploy job; the
  old pods serve meanwhile, so users do not notice, but a rollout takes longer.
* **Every pod start checks the schema** (a lock and one query on the history: 0.4 s on the local cluster, a few
  seconds on a small, cold node); a pod restart or a scale-up therefore needs the database, which it needs anyway to
  become ready.
* **A failed migration is seen in the pod's log** (`kubectl logs deployment/api`), not in the deploy job's own output;
  the job still fails, at the rollout wait.

## Revision (2026-10-19): one GitHub repository, GitHub Actions

**Status:** accepted. Replaces where the code lives and what runs the CI/CD in every earlier section: the three GitLab
projects, their `.gitlab-ci.yml` pipelines, the GitLab registry, CI/CD variables, deploy tokens, the job token and the
pipeline schedule. The layout of `deploy/`, GitOps with `deploy/` as the only writer to the clusters, the pull model
with SemVer versions, the migrations at startup, the shared infrastructure and the prepared Argo CD objects are
unchanged.

### Context

The system lived in three GitLab projects (`zeiss/backend`, `zeiss/frontend`, `zeiss/deploy`), each with its own
pipeline and container registry; locally they were already three folders side by side. It moved to one public GitHub
repository, [RubenCipriano/zeiss-product-management](https://github.com/RubenCipriano/zeiss-product-management), with
the same three folders. This revision records what changed with the move; the move itself was the owner's choice.

### Decision

**One repository, three folders.** `backend/`, `frontend/` and `deploy/` (and `docs/`, `tools/`) are folders of one
clone, so `deploy/scripts/deploy.sh local` finds `../backend` and `../frontend` without cloning anything else. The
Argo CD Applications point at `git@github.com:RubenCipriano/zeiss-product-management.git`, paths
`deploy/environments/staging` and `deploy/environments/production`.

**GitHub Actions instead of GitLab CI** (`.github/workflows/`, GitHub-hosted `ubuntu-24.04` runners, each workflow
filtered to its folder's paths):

| Before (GitLab) | Now (GitHub Actions) |
| --- | --- |
| `backend/.gitlab-ci.yml`: `test:backend`, `build:api`, `migrations:sql`, `scan:api`, `publish:api`, `release` | workflow Backend (`backend.yml`): `test`, `build`, `migrations-sql` (artifact `migrations-sql`, 90 days), `scan`, `publish`, `release` |
| `frontend/.gitlab-ci.yml`: `test:frontend`, `build:web`, `build:gateway`, `scan:web`, `scan:gateway`, `publish:web`, `publish:gateway`, `release` | workflow Frontend (`frontend.yml`): `test`, `build` and `scan` (a matrix over web and gateway), one `publish` for both images, `release` |
| `deploy/.gitlab-ci.yml`: `validate:manifests`, `validate:scripts`, `validate:alloy` | workflow Deploy (`deploy.yml`): `validate-manifests`, `validate-scripts`, `validate-alloy` |
| `deploy:staging` + `smoke:staging` | `deploy-staging`: deploy, then the smoke check, in one job |
| `deploy:production` (manual) + `smoke:production` | `deploy-production`: waits for a required reviewer of the GitHub environment `production` |
| `infra:staging`, `infra:production` (manual) | `infra-staging`, `infra-production`: Run workflow on Deploy, input "staging infra" / "production infra" (also "staging apps" / "production apps" to redeploy) |
| `update:staging` on a pipeline schedule (every 15 minutes) | workflow Update staging (`update-staging.yml`): after every successful Backend or Frontend run of a version tag, or by hand; no schedule (one can be added) |
| `propose:production` (manual, merge request through git push options) | workflow Propose production (`propose-production.yml`): pushes a branch and opens a pull request through the REST API |
| GitLab SAST and Secret Detection templates | workflow CodeQL (`codeql.yml`: C#, TypeScript, the workflows); GitHub secret scanning and push protection |
| Trivy report in GitLab's security widget | Trivy SARIF in Security → Code scanning (categories `trivy-api`, `trivy-web`, `trivy-gateway`); still fails on fixable critical vulnerabilities |
| JUnit reports in the merge request | artifacts `backend-test-results`, `frontend-test-results` |
| Docker-in-Docker service for Testcontainers and builds (with an MTU workaround) | the runner's own Docker; buildx builds `linux/amd64` and `linux/arm64` without emulation |

**Two release lines in one repository.** Tags `backend/vX.Y.Z[-rc.N]` and `frontend/vX.Y.Z[-rc.N]` (were `vX.Y.Z` in
each project); the image tags stay `X.Y.Z`, so `deploy/` and Argo CD Image Updater see no difference. Releases are
GitHub Releases (the `gh` CLI; a pre-release for `-rc.N`), whose notes list only the commits that changed that
folder since the previous tag of the same line. Tag rulesets (Settings → Rules → Rulesets) replace protected tags.

**Registry ghcr.io.** `ghcr.io/rubencipriano/zeiss-product-management/{api,web,gateway}`; commit builds are tagged with
the first 8 characters of the commit; the layer cache is GitHub Actions' cache (`type=gha`) instead of a `buildcache`
tag in the registry. The Backend and Frontend workflows publish with their own `GITHUB_TOKEN`: they still need no
secret, and still hold no credential of a cluster.

**Pulling images: a token of a user.** The pull Secret is now `registry-credentials`, made from `REGISTRY_USER` and
`REGISTRY_PASSWORD`, a GitHub user and a classic personal access token with `read:packages`, stored as secrets of the
GitHub environments `staging` and `production` (the GitLab group deploy token and `CI_DEPLOY_USER` /
`CI_DEPLOY_PASSWORD` are gone). The run's `GITHUB_TOKEN` cannot be used: it expires with the run, and the nodes pull
long after.

**Configuration: repository variables and environment secrets.** `STAGING_ENABLED`, `PRODUCTION_ENABLED`,
`STAGING_URL`, `PRODUCTION_URL` are repository variables; `KUBECONFIG`, `REGISTRY_USER`, `REGISTRY_PASSWORD`,
`API_SIGNING_KEY`, `API_CONSUMERS` (and optionally `SMOKE_CLIENT_*`, `LOKI_*`) are secrets of each environment. A
composite action (`.github/actions/environment-secrets`) writes the kubeconfig, key and consumers to files under the
runner's temporary directory, as `deploy.sh` expects them. The GitLab agent for Kubernetes (and `KUBE_CONTEXT` as its
path) no longer applies: a job reaches its cluster only through `KUBECONFIG`.

**Pushes from the workflows.** Update staging pushes its version commit to `main` with `GITHUB_TOKEN` as
`github-actions[bot]`, which needs `main` unprotected or a ruleset bypass for GitHub Actions (or a repository secret
`DEPLOY_PUSH_TOKEN`). As with GitLab's job token, a push made with `GITHUB_TOKEN` starts no workflow, so Update staging
deploys its own commit (under the same concurrency group `staging` as `deploy-staging`). Propose production needs
"Allow GitHub Actions to create and approve pull requests"; a pull request it opens starts no workflow either.

### Consequences

* **Staging follows a release at once** (the tag's run ends, Update staging starts) instead of within the schedule's
  15 minutes; a version published any other way is noticed only by a manual run, or a schedule if one is added.
* **Holding staging on an older version** is the repository variable `STAGING_HOLD=true`: `update-staging` then does
  nothing while `deploy-staging` keeps applying commits; without it, the next release moves staging forward.
* **The production approval is the environment's required reviewers**, the same for `deploy-production` and
  `infra-production`, instead of who may start a manual job.
* **A user's token pulls the images.** The classic personal access token in `REGISTRY_PASSWORD` belongs to a person
  and expires; it must be rotated (and replaced when that person leaves), where the group deploy token was the group's.
* **One clone, one history.** A change to the API and the deploy manifests can be one pull request; the path filters
  keep each workflow to its folder, and the release notes to their folder's commits.
* **Not yet run end to end.** The workflows pass `actionlint`; the deploy jobs need clusters, which do not exist yet.
