# Deploy

Everything that runs on the Kubernetes clusters of the Product Management system, and the only place that changes
them. Each environment is a folder here; what is committed is what runs (GitOps):

* the application: the API (from [backend](https://gitlab.com/zeiss/backend)), the web app and the gateway (from
  [frontend](https://gitlab.com/zeiss/frontend)), in the namespace `product-management`;
* the shared infrastructure that any service on the cluster can use: PostgreSQL, Redis and the logs (Loki, Grafana,
  Alloy), in the namespace `infra`.

The backend and frontend pipelines build, test and scan their images and then **release** them by committing the new
image tag to this repository. They never apply anything. This repository's pipeline applies.

## Where it runs

The whole system runs on one machine with `scripts/infra.sh up` and `scripts/deploy.sh local`, on a Kubernetes that can
run the images built there: Docker Desktop's on macOS and Windows, or k3s on an Ubuntu server. The scripts are the
same everywhere (bash, GNU or BSD tools).

| | macOS | Windows | Ubuntu server (22.04, 24.04) |
| --- | --- | --- | --- |
| Kubernetes | Docker Desktop, Kubernetes enabled | Docker Desktop (WSL 2 backend), Kubernetes enabled | k3s v1.34.1+k3s1, one node, installed by `scripts/setup-ubuntu.sh` |
| Shell | Terminal | an Ubuntu shell in WSL 2 | ssh |
| You install | Docker Desktop; `git`, `openssl`, `python3`, `curl` come with macOS (Xcode command line tools) | Docker Desktop with WSL integration for Ubuntu; in Ubuntu: `sudo apt install -y git openssl python3 curl` | nothing: `scripts/setup-ubuntu.sh` installs Docker Engine 29.9.0, k3s (with `kubectl`), `git`, `openssl`, `python3`, `curl`, `jq` |
| Resources | Docker Desktop: 4 CPUs, 8 GB of memory | the same | 4 CPUs, 8 GB of memory, 30 GB of disk |
| `kubectl` context | `docker-desktop` | `docker-desktop` | `default` |
| Images | built by Docker Desktop, run as they are | the same | built by Docker Engine, copied into k3s (`docker save \| sudo k3s ctr images import`) |
| Application | http://localhost:8080 | http://localhost:8080 | http://localhost:8080 on the server, http://&lt;server-ip&gt;:8080 from elsewhere (k3s' ServiceLB) |
| Verified | yes, every change | the scripts' portability (no macOS-only commands); not run on Windows | yes, end to end on a fresh Ubuntu 24.04 VM: `scripts/smoke.sh`, the 14 requirement checks and the 22 Playwright tests from a laptop, a start over, a backup |

`scripts/deploy.sh local` tells the two kinds of cluster apart by the context (`docker-desktop`) or by a k3s node
named like this machine; `LOCAL_CLUSTER=docker-desktop|k3s` overrides that. Any other cluster (a remote k3s, a cloud
cluster) cannot run images built on the laptop: release them through the pipelines and deploy staging or production.

**macOS.** Docker Desktop → Settings → Kubernetes → Enable Kubernetes; Resources: 4 CPUs, 8 GB. Then the
[local quick start](#local-quick-start).

**Windows.** Docker Desktop with the WSL 2 backend → Settings → Kubernetes → Enable Kubernetes; Settings → Resources →
WSL integration → on for Ubuntu (it puts `docker` and `kubectl` in that shell). Work in the Ubuntu shell and clone
into its home directory (`~`, not `/mnt/c`: Linux line endings and file modes, and much faster builds). If
`kubectl config get-contexts` there has no `docker-desktop`, use Windows' kubeconfig:
`mkdir -p ~/.kube && cp /mnt/c/Users/<you>/.kube/config ~/.kube/config`. Then the
[local quick start](#local-quick-start); the browser on Windows opens http://localhost:8080. (Git Bash works in
principle too, with `python3` on the `PATH`; the scripts are checked out with LF line endings by `.gitattributes`.)

**Ubuntu server.** A fresh server with sudo and internet access:

```bash
git clone https://gitlab.com/zeiss/backend.git
git clone https://gitlab.com/zeiss/frontend.git
git clone https://gitlab.com/zeiss/deploy.git
cd deploy
scripts/setup-ubuntu.sh          # Docker Engine, k3s and kubectl, pinned; about 2 minutes; safe to run again
newgrp docker                    # you were added to the group docker: start a shell in it (or log in again)

scripts/infra.sh up              # as on Docker Desktop (k3s brings its own metrics-server)
scripts/deploy.sh local          # builds the images, copies them into k3s → http://<server-ip>:8080
scripts/smoke.sh http://localhost:8080
```

On k3s the volumes are directories on the server (local-path; `postgres-backups` stays `Pending` until the first
backup uses it), and the NetworkPolicies are enforced (Docker Desktop ignores them). From a laptop, `kubectl` reaches
the server with a copy of its kubeconfig: `scp <user>@<server-ip>:.kube/config ~/.kube/zeiss-k3s`, replace
`127.0.0.1` in it with the server's address (`sed -i.bak 's/127.0.0.1/<server-ip>/' ~/.kube/zeiss-k3s`), then
`KUBECONFIG=~/.kube/zeiss-k3s kubectl -n infra port-forward svc/loki 3100:3100` (e.g. for the browser-errors
end-to-end test). If `ufw` is on, open the ports: `sudo ufw allow 8080/tcp` (and `6443/tcp` for `kubectl`).

**Its own GitLab Runner.** The same server can run the pipelines' jobs instead of gitlab.com's shared runners.
Install the runner from GitLab's repository (`curl -L "https://packages.gitlab.com/install/repositories/runner/gitlab-runner/script.deb.sh" | sudo bash`,
then `sudo apt install -y gitlab-runner`), create a runner in the project or group (Settings → CI/CD → Runners → New
runner, which shows a `glrt-` token) and register it with the Docker executor, so the jobs keep their `image:`:
`sudo gitlab-runner register --non-interactive --url https://gitlab.com --token <glrt-token> --executor docker --docker-image alpine:3.22`.
The jobs reach this k3s through the File variable `KUBECONFIG`: the server's `~/.kube/config` with `127.0.0.1`
replaced by its address (inside a job's container `127.0.0.1` is the container itself). A `shell` executor works too,
but then the tools of every job (`kubectl`, `kubeconform`, `yq`, `jq`, `shellcheck`) must be installed on the server.

## Why this layout

* **One source of truth for what runs where.** Every environment is a folder; the image tags in it are what runs.
  "What is in production?" is answered by reading a file, and its history is the release history.
* **Apps build, deploy applies.** Developers change backend and frontend; only this repository (and later Argo CD)
  changes the clusters. App pipelines need no cluster rights except the backend's migration step.
* **Promotion and rollback are git operations.** Staging gets every release automatically; production changes through
  a merge request that someone approves; a rollback is a `git revert`.
* **Ready for Argo CD.** The pipeline's apply step is exactly what an Argo CD Application per environment would do;
  switching only moves who applies (see [Argo CD](#argo-cd-next-step)).
* **Shared infrastructure, plain names.** PostgreSQL, Redis and the logs run once per cluster in the namespace `infra`
  (`postgres`, `redis`, `loki`, `grafana`) and serve every service; a new service gets its own database with
  `scripts/create-database.sh`. Dedicated machines are added as Kubernetes nodes with a role
  (`scripts/add-node.sh`), not as servers outside the cluster.
* **Configuration you can read.** Every setting is a plain env key in the YAML (connection strings without
  passwords, the API address, rate limits); only passwords, the signing key and the accounts are Secrets, and none of
  them is committed.

## Status

| | |
| --- | --- |
| Verified locally (Docker Desktop Kubernetes) | `scripts/infra.sh up`, `scripts/deploy.sh local`: the API, web and gateway with the shared infrastructure, 850k products and a 20M-row stock ledger; all of the assessment's requirements, end-to-end tests (Playwright), load tests (k6) and the log pipeline (Loki, Grafana, 12 alert rules) pass against it |
| Verified on k3s (a fresh Ubuntu 24.04 VM) | `scripts/setup-ubuntu.sh`, `scripts/infra.sh up`, `scripts/deploy.sh local`, a start over from nothing; the smoke check and all of the assessment's requirements pass through http://&lt;server-ip&gt;:8080 |
| Validated, not yet run on a real cluster | `environments/staging` and `environments/production` (rendered and schema-checked by `scripts/validate.sh`), the GitLab pipelines (schema-checked; jobs that need no cluster run on gitlab.com), `scripts/add-node.sh` (dry run) |
| Still to provide per environment | a k3s (or any) cluster, the hosts in `ingress.yaml` and `STAGING_URL` / `PRODUCTION_URL` (now `*.example.com`), the CI/CD variables below, TLS issuer, off-node backups |

## Layout

| Path | What it is |
| --- | --- |
| `apps/backend/` | the API: Deployment (its configuration as env keys), headless Service `api`, autoscaler, disruption budget, NetworkPolicy, and the namespace `product-management` |
| `apps/frontend/` | the web app (`web.yaml`) and the gateway (`gateway.yaml`): Deployments, Services, autoscaler, disruption budgets, NetworkPolicy |
| `environments/local/` | a cluster on one machine (Docker Desktop's Kubernetes, or k3s on an Ubuntu server): apps + local patches (connection strings to `postgres.infra` / `redis.infra`, Swagger on, gateway as a `LoadBalancer` on http://localhost:8080, `COLLECTOR_UPSTREAM` to `alloy-faro.infra`), the API's keys generated on this machine (`generated/`, never committed) |
| `environments/staging/` | apps + Ingress (TLS) + the registry pull Secret + `images:` with the released tags; **the file the app pipelines update** |
| `environments/production/` | the same; changed only through merge requests |
| `infra/` | `postgres.yaml` (PostgreSQL 18, nightly `pg_dump`), `redis.yaml` (Redis 8), `logs.yaml` (Loki, Grafana with dashboards and 12 alert rules, Alloy), `dashboards/` |
| `scripts/deploy.sh` | deploys an environment: `scripts/deploy.sh <local\|staging\|production> [infra\|apps\|all]` |
| `scripts/infra.sh` | starts, stops and shows the shared infrastructure |
| `scripts/create-database.sh` | gives a service its database, two users and a Secret with their passwords |
| `scripts/add-node.sh` | joins a machine to a k3s cluster as a `database`, `cache` or `apps` node |
| `scripts/setup-ubuntu.sh` | prepares an Ubuntu server for `deploy.sh local`: Docker Engine, a single-node k3s, `kubectl` |
| `scripts/local-secrets.sh` | the API's local signing key and demo consumers (and the .NET user-secrets for `dotnet run`) |
| `scripts/validate.sh` | renders and checks every environment and `infra/` (kubeconform and the project's rules) |
| `scripts/smoke.sh` | checks a deployed environment from outside, through its URL |
| `.gitlab-ci.yml` | validate every change; apply staging (automatic) and production (manual); smoke checks; infra (manual) |

## How a change reaches an environment

```mermaid
flowchart LR
  subgraph backend["backend pipeline"]
    B1[test, build, scan<br/>api image] --> B2[migrate:staging<br/>one-off pod] --> B3[release:staging]
    B3 -.manual.-> B4[migrate:production] --> B5[release:production]
  end
  subgraph frontend["frontend pipeline"]
    F1[test, build, scan<br/>web + gateway images] --> F3[release:staging]
    F3 -.manual.-> F5[release:production]
  end
  subgraph deploy["deploy repository"]
    S[environments/staging<br/>kustomization.yaml]
    MR[merge request<br/>environments/production]
    P[environments/production<br/>kustomization.yaml]
    D1[deploy:staging + smoke]
    D2[deploy:production<br/>manual + smoke]
  end
  B3 -- "commit: backend: api &lt;sha&gt;" --> S
  F3 -- "commit: frontend: web, gateway &lt;sha&gt;" --> S
  B5 -- push branch + MR --> MR
  F5 -- push branch + MR --> MR
  MR -- merge --> P
  S --> D1
  P --> D2
```

* **Staging.** On the default branch, the backend pipeline migrates staging's database with the new API image and then
  commits `backend: api <sha>` to `environments/staging/kustomization.yaml` here (the image's `newTag`); the frontend
  pipeline does the same for `web` and `gateway`. Each commit starts this repository's pipeline: `deploy:staging`
  applies `environments/staging` and waits for the rollouts, `smoke:staging` checks it through its URL.
* **Production.** Promotion is a merge request. The app pipelines' manual `release:production` job pushes a branch
  that copies the tag staging runs into `environments/production/kustomization.yaml` and opens a merge request
  ("Release api `<sha>` to production"). Merging it is the release decision; `deploy:production` (manual, protected)
  then applies it. Production's migration runs in the backend pipeline before the merge request is opened.
* **Rollback.** `git revert` the release commit (staging) or the merge (production) on `main`: the pipeline applies the
  previous tags. Schema changes are not rolled back: every migration works with the previous release
  (expand/contract), and a bad migration is fixed forward.
* **History.** `git log environments/staging` is staging's deployment history: who released which commit, when.

## Local quick start

Clone the three repositories side by side (`scripts/deploy.sh local` builds the images from `../backend` and
`../frontend`), on a machine prepared as in [Where it runs](#where-it-runs):

```bash
git clone https://gitlab.com/zeiss/backend.git
git clone https://gitlab.com/zeiss/frontend.git
git clone https://gitlab.com/zeiss/deploy.git
cd deploy
kubectl config use-context docker-desktop    # macOS, Windows; on the Ubuntu server: default (k3s)

scripts/infra.sh up              # namespace infra: PostgreSQL, Redis, Loki + Grafana + Alloy (and metrics-server)
scripts/deploy.sh local          # the API, web app and gateway → http://localhost:8080 (k3s: also http://<server-ip>:8080)
```

Sign in with `viewer`/`viewer`, `clerk`/`clerk` or `manager`/`manager`. The first `scripts/deploy.sh local` also
creates the service's database (`scripts/create-database.sh`) and the API's local keys (`scripts/local-secrets.sh`).

Prerequisites: Docker Desktop with Kubernetes (or, on Ubuntu, what `scripts/setup-ubuntu.sh` installs), `kubectl`,
`openssl`, `python3` and `curl`. The images are built in Docker, so neither .NET nor Node is needed to deploy.

```bash
kubectl diff -k environments/local                         # what differs from the last deploy (nothing, normally)
scripts/validate.sh                                        # needs kubectl, kubeconform, yq, jq; or:
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 scripts/validate.sh
scripts/smoke.sh http://localhost:8080
kubectl -n product-management port-forward deployment/api 5080:8080   # Swagger → http://localhost:5080/swagger
kubectl -n infra port-forward svc/grafana 3000:3000                   # Grafana → http://localhost:3000
```

## Clean deploy

### Start over locally

Removes everything a previous deploy left, **including every row in the database and every log line**, and deploys
from nothing. An empty database gets the sample catalogue (24 products) from the migrations.

```bash
kubectl config use-context docker-desktop                         # on the Ubuntu server: default
kubectl delete namespace product-management --ignore-not-found   # the API, web app, gateway and their Secrets
scripts/infra.sh down --delete-data                               # PostgreSQL, Redis, logs, with their volumes and passwords
rm -rf environments/local/generated                               # the local signing key, demo accounts and image tags

scripts/infra.sh up                                               # fresh infrastructure and random passwords
scripts/deploy.sh local                                           # database + users, keys, images, migrations, apply
scripts/smoke.sh http://localhost:8080                            # 4 checks through the gateway
```

To keep the data and only rebuild the application, skip the first four commands. The commands are the same on every
platform; only the context differs (`docker-desktop`, or `default` for k3s), and on the Ubuntu server the application
is also on http://&lt;server-ip&gt;:8080. k3s itself stays installed (to remove it too: `/usr/local/bin/k3s-uninstall.sh`,
then `scripts/setup-ubuntu.sh` installs it again).

### A new environment (staging or production), the first time

1. **Cluster:** k3s (or any Kubernetes) with an Ingress controller and cert-manager; machines for the database or
   the cache join it with `scripts/add-node.sh <user@host> database|cache|apps`.
2. **GitLab:** the group deploy token `gitlab-deploy-token` (`read_registry`); in this project `KUBE_CONTEXT` or
   `KUBECONFIG`, `API_SIGNING_KEY` and `API_CONSUMERS` for the environment; in backend and frontend
   `DEPLOY_REPO_TOKEN`; the real hosts in `environments/<environment>/ingress.yaml` and `STAGING_URL` /
   `PRODUCTION_URL`.
3. **Infrastructure:** run the manual job `infra:<environment>` (or `scripts/infra.sh up` with kubectl pointing at the
   cluster), then `scripts/create-database.sh product-management product_management pm_app pm_migrator`.
4. **Switch the environment on:** `STAGING_ENABLED=true` (later `PRODUCTION_ENABLED=true`) in the three projects.
5. **Release:** a pipeline on backend's `main` migrates and commits the API tag; one on frontend's `main` commits the
   web and gateway tags. Until both have released, this project's `deploy:staging` refuses the `unreleased` tags; the
   second release commit deploys everything and `smoke:staging` checks it.
6. **Production:** start `migrate:production` in backend and `release:production` in frontend; each opens a merge
   request here. Merging them and starting `deploy:production` deploys production.

## `scripts/deploy.sh`

```bash
scripts/deploy.sh <local|staging|production> [infra|apps|all]     # default: apps
```

| Target | Does |
| --- | --- |
| `infra` | `scripts/infra.sh up`: the shared infrastructure in `infra` |
| `apps` | the API, the web app and the gateway from `environments/<environment>` |
| `all` | `infra`, then `apps` |

**local** (Docker Desktop's Kubernetes, or k3s on this machine): creates the local keys if missing; builds
`product-management/api`, `web` and `gateway` from `../backend` and `../frontend` with Docker, tagged with each
checkout's commit (plus `-dirty-<time>` with uncommitted changes), copies them into k3s' containerd on k3s
(`docker save | sudo k3s ctr images import`; Docker Desktop runs them as they are), and records the tags in `environments/local/generated/build/` (not committed) so that
`kubectl diff -k environments/local` compares against exactly what was applied; creates the database on the first
run; copies the shared Redis' password into `redis-credentials`; runs the migrations with the new API image as a
one-off pod in the cluster (as the schema owner, from `database-credentials`); applies `environments/local`; waits
for the rollouts.

**staging, production** (the pipeline, or a laptop whose `kubectl` reaches that cluster): refuses an environment whose
images are still `newTag: unreleased`; creates the registry credential `gitlab-registry` and the API's Secret
`api-secrets` from the variables below when they are set; copies the Redis password; applies the environment exactly
as committed; waits for the rollouts and restarts the API when one of its Secrets changed. It does not migrate: the
backend pipeline does that before it releases an image.

| Variable | For | Effect |
| --- | --- | --- |
| `KUBE_CONTEXT` | all | the `kubectl` context (the GitLab agent's `<project>:<agent>`); default: the current one |
| `BACKEND_DIR`, `FRONTEND_DIR` | local | other checkouts to build (default `../backend`, `../frontend`) |
| `LOCAL_CLUSTER` | local | `docker-desktop` or `k3s`: how the images reach the cluster, when the detection is wrong |
| `LOGS=off` | local | no log collector: the gateway answers the browser's error reports with 204 |
| `SEED_SAMPLE_DATA` | local | the sample catalogue on an empty database (default `true`) |
| `CI_DEPLOY_USER`, `CI_DEPLOY_PASSWORD` | staging, production | a deploy token with `read_registry` on both registries → Secret `gitlab-registry` |
| `API_SIGNING_KEY`, `API_CONSUMERS` | staging, production | files: the ES256 private key (PEM) and `consumers.json` → Secret `api-secrets` |

## Environments

| | local | staging | production |
| --- | --- | --- | --- |
| Cluster | Docker Desktop (`docker-desktop`) or a single-node k3s (`default`) | its k3s cluster, through the GitLab agent or a kubeconfig | its own |
| Images | built on that machine from `../backend`, `../frontend` | `registry.gitlab.com/zeiss/{backend/api,frontend/web,frontend/gateway}:<sha>`, set by the app pipelines | the same, set by merged merge requests |
| Entry | `LoadBalancer` on http://localhost:8080 (k3s: also the server's address) | Ingress, TLS by cert-manager: `staging.products.example.com` | `products.example.com` |
| Swagger | on | on (port-forward) | off |
| API keys (`api-secrets`) | `generated/`, made by `scripts/local-secrets.sh` | CI/CD variables `API_SIGNING_KEY`, `API_CONSUMERS` | the same, protected |
| Database, Redis | `postgres.infra`, `redis.infra`; passwords in the cluster (`database-credentials`, `redis-credentials`) | the same | the same |

Replace the `example.*` hosts in `environments/*/ingress.yaml` and `STAGING_URL` / `PRODUCTION_URL` in
`.gitlab-ci.yml` with the real ones; the Ingress needs cert-manager and a ClusterIssuer `letsencrypt`.

**What the app pipelines need from this repository:** a project access token (Settings → Access tokens) with the
role **Maintainer** (to push to the protected `main`) and the scope `write_repository`, stored in the backend and
frontend projects as the masked, protected variable `DEPLOY_REPO_TOKEN`. And the database of each environment, made
once: `scripts/create-database.sh product-management product_management pm_app pm_migrator` against that cluster.

## Shared infrastructure

```bash
scripts/infra.sh up                          # everything: postgres, redis, logs
scripts/infra.sh up postgres redis           # only these two (no logs)
scripts/infra.sh down logs                   # remove a component; its volumes and passwords are kept
scripts/infra.sh down postgres --delete-data # ... and delete its volumes and password too
scripts/infra.sh status                      # pods, services, volumes, and how to reach them
```

* **`up`** creates, once, what must never be committed: the namespace and the random passwords (Secrets
  `postgres-admin` and `redis-auth`), and installs metrics-server (v0.8.0) where the cluster has none (the autoscalers
  need it; k3s ships it, Docker Desktop does not). Then it applies the component and waits until it is ready. A
  changed `redis.conf`, Loki, Grafana or Alloy configuration restarts that component; a changed `postgresql.conf`
  prints how to reload (or, for memory settings, restart) PostgreSQL.
* **`down`** deletes a component's workloads, Services and configuration; its volumes (`data-postgres-0`,
  `postgres-backups`, `data-loki-0`) and password Secrets stay unless `--delete-data` is given.
* Nothing is published on the host: `kubectl -n infra port-forward svc/grafana 3000:3000`, `svc/loki 3100:3100`;
  `kubectl -n infra exec -it statefulset/postgres -- psql -U postgres -d product_management`;
  `kubectl -n infra exec -it deployment/redis -- redis-cli`.

**A service's database.** One PostgreSQL for the platform, one database per service:
`scripts/create-database.sh <service-namespace> <database> [app-user] [migrator-user] [--rotate]`. It creates,
idempotently, the database, the **migrator** user (owns the schema, runs the migrations), the **app** user (rows only;
statements end after 30 s, idle transactions after 60 s), `pg_stat_statements`, and the Secret `database-credentials`
in the service's namespace (`host`, `port`, `database`, `app-user`, `app-password`, `migrator-user`,
`migrator-password`). Passwords are random and kept on later runs; `--rotate` makes new ones (then restart the
service's pods).

**Cache and logs.** Redis is `redis.infra.svc.cluster.local:6379` with one password (`redis-auth`), which
`scripts/deploy.sh` copies into the service's namespace. Alloy collects the stdout of every pod: in Loki they are
`{namespace="<namespace>", app="<app.kubernetes.io/name>"}`. The gateway forwards the browser's error reports to
`alloy-faro.infra.svc.cluster.local:12347`.

**Another Loki** (e.g. Grafana Cloud):
`LOKI_URL=https://<host>/loki/api/v1/push LOKI_USERNAME=<user> LOKI_PASSWORD=<token> scripts/infra.sh up logs`. Then
only Alloy runs in the cluster and pushes there; `scripts/infra.sh up logs` without `LOKI_URL` switches back. Without
logs at all (`scripts/infra.sh up postgres redis`), deploy with `LOGS=off scripts/deploy.sh local`.

**Backups.** The CronJob `postgres-backup` dumps the roles and every database at 02:30 into the volume
`postgres-backups` and keeps 7 days (`kubectl -n infra create job --from=cronjob/postgres-backup postgres-backup-now`
for one now). It protects against mistakes, not against losing the node: production ships the dumps off the node or
uses volume snapshots.

## Nodes

On Docker Desktop, and on the k3s of `scripts/setup-ubuntu.sh`, everything shares one node. A k3s cluster can give the
database and the cache machines of their own (`scripts/deploy.sh local` copies its images only into the k3s of the
machine it runs on, so a cluster of several machines runs registry images, as staging and production do):

```bash
scripts/add-node.sh admin@db-1.example.internal database --server https://k3s-1.example.internal:6443
scripts/add-node.sh admin@cache-1.example.internal cache
scripts/add-node.sh admin@apps-1.example.internal apps
scripts/add-node.sh admin@db-1.example.internal database --dry-run      # print the steps only
```

Over ssh it installs the k3s agent (pinned, v1.34.1+k3s1) with the server's join token, labels the node
`infra/role=<role>` and taints `database` and `cache` nodes, so only PostgreSQL (and Redis) run there; both prefer
such a node and run anywhere without one. A volume stays on its node: add the database node before the first
`scripts/infra.sh up postgres`, or move the database with a dump and restore.

## CI/CD (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml):

| Job | When | Does |
| --- | --- | --- |
| `validate:manifests` | every merge request and commit | `scripts/validate.sh`: every environment and `infra/` render and pass kubeconform; components labelled; no committed Secret outside the local generator; staging and production pull with `gitlab-registry` and use registry images |
| `validate:scripts` | every merge request and commit | shellcheck on `scripts/*.sh` |
| `validate:alloy` | every merge request and commit | `alloy validate` and `alloy fmt` on the configuration inside `infra/logs.yaml` |
| `deploy:staging` → `smoke:staging` | `main`, when `environments/staging`, `apps/`, `infra/` or `scripts/deploy.sh` changed | `scripts/deploy.sh staging apps`, then `scripts/smoke.sh $STAGING_URL` |
| `deploy:production` → `smoke:production` | `main`, when `environments/production` or `apps/` changed; **manual** | `scripts/deploy.sh production apps`, then the smoke check |
| `infra:staging`, `infra:production` | `main`, **manual** | `scripts/deploy.sh <environment> infra` |

The smoke check goes through the public URL: `/healthz` (gateway), `/` (web app), an unknown client on
`/api/auth/token` answered `401 invalid_client` by the API and, with `SMOKE_CLIENT_ID` / `SMOKE_CLIENT_SECRET`, a
signed-in `GET /api/categories` (API and database).

CI/CD variables of this project:

| Variable | Scope | What |
| --- | --- | --- |
| `STAGING_ENABLED`, `PRODUCTION_ENABLED` | project | `"true"` creates that environment's jobs; without them the pipeline only validates |
| `KUBE_CONTEXT` | staging, production | `<agent configuration project path>:<agent name>` (the GitLab agent for Kubernetes) |
| `KUBECONFIG` | staging, production | File, instead of `KUBE_CONTEXT`: a kubeconfig whose current context is the cluster |
| `API_SIGNING_KEY` | staging, production; File, protected | the API's ES256 private key (`docker run --rm <api image> generate-signing-key`) |
| `API_CONSUMERS` | staging, production; File, protected | `consumers.json`: client ids, PBKDF2 hashes (`<api image> hash-secret <secret>`), roles |
| `SMOKE_CLIENT_ID`, `SMOKE_CLIENT_SECRET` | staging, production; optional, masked | a viewer consumer for the signed-in smoke check |
| `LOKI_URL`, `LOKI_USERNAME`, `LOKI_PASSWORD` | staging, production; optional | another Loki, for the infra jobs |
| `STAGING_URL`, `PRODUCTION_URL` | in `.gitlab-ci.yml`; override in the project | the environments' public URLs |

The registries are private. Create **one group deploy token** in the `zeiss` group (Settings → Repository → Deploy
tokens) named exactly `gitlab-deploy-token` with the scope `read_registry`: it reads both the backend's and the
frontend's registry, and GitLab passes it to every job of the group's projects as `CI_DEPLOY_USER` /
`CI_DEPLOY_PASSWORD`. `scripts/deploy.sh` stores it in the cluster as the Secret `gitlab-registry`, which the API,
web and gateway Deployments pull with (the backend's migrate job creates the same Secret on a first release). No
database or Redis password is a variable: they are random and live in the cluster.

## Argo CD (next step)

The pipeline applies today; Argo CD can take that over without changing this repository's layout:

1. Install Argo CD in the cluster (`kubectl create namespace argocd` and its pinned install manifest) and give it
   read access to this repository (a deploy token with `read_repository`).
2. One `Application` per environment, pointing at `environments/<environment>` on `main`:

   ```yaml
   apiVersion: argoproj.io/v1alpha1
   kind: Application
   metadata: { name: product-management-staging, namespace: argocd }
   spec:
     project: default
     source: { repoURL: https://gitlab.com/zeiss/deploy.git, targetRevision: main, path: environments/staging }
     destination: { server: https://kubernetes.default.svc, namespace: product-management }
     syncPolicy:
       automated: { prune: true, selfHeal: true }      # staging: every commit is applied
   ```

   Production the same with `path: environments/production` and no `automated` block: a person presses Sync after
   the release merge request is merged.
3. In `.gitlab-ci.yml` remove the deploy and smoke jobs (the pipeline keeps only validate); the Secrets that
   `scripts/deploy.sh` creates from CI/CD variables (`gitlab-registry`, `api-secrets`, `redis-credentials`) move to a
   secrets operator (e.g. External Secrets) or stay a one-off step per environment.
4. Nothing changes in the app pipelines: they still commit tags here, and the migrations stay in the backend
   pipeline, before the release commit. `infra/` can become a third Application with manual sync.
