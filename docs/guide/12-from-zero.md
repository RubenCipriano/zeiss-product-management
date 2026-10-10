# 12. From zero to production

**After this chapter you will know** how to take this repository from an empty GitLab group and a set of fresh
machines to a running staging and production: which servers to create and how they talk to each other, how TLS is
set up at the edge and inside the private network, how backups and restores work, how GitLab is configured (one
project per part, runners, the agent for Kubernetes, environments, every CI/CD variable per project), the order of
the first deployment, the day-2 procedures (rotating secrets, scaling, upgrading, restoring, rolling back), and a
checklist for go-live.

The repository's three parts become three GitLab projects in one group: **infra** (the servers outside the cluster
and the cluster's log collector), **backend** (the API) and **frontend** (the web app and the gateway). Each has its
own `.gitlab-ci.yml`, registry and variables ([chapter 9](09-deployment-and-operations.md#the-layout-three-parts)).

It is a runbook: commands to copy, in order. It is also honest about what has been exercised. Each section is marked:

* **Tested here:** run in this repository, locally or in CI (the local stand-ins of the same servers, the same
  scripts and manifests).
* **Procedure:** the standard way to do it, written for this system but not executed against real servers as part
  of this assessment. Versions and flags are current at the time of writing; check them against the vendors' docs.

All names below (`example.com`, `example.internal`, `10.0.x.x`) are placeholders.

---

## The target

```text
                      Internet
                         │ 443 (HTTPS)
                ┌────────▼─────────┐
                │  k3s cluster     │   Traefik (Ingress, TLS by cert-manager) → gateway → API / web
                │  3 server nodes  │   Alloy (pod logs → Loki)
                └──┬──────┬─────┬──┘
     5432 (TLS)    │      │6379 │ 3100
          ┌────────▼┐  ┌──▼────▼─┐  ┌─────────────────┐
          │ pm-db   │  │ pm-cache│  │ pm-monitoring   │   Grafana 3000 (operators, SSO/VPN)
          │ PG 18   │  │ Redis 8 │  │ Grafana + Loki  │   Loki 3100 (logs from the cluster and the servers)
          └────┬────┘  └─────────┘  └─────────────────┘
               │ WAL + base backups
          ┌────▼─────────────┐        ┌──────────────────────────────┐
          │ object storage   │        │ GitLab runner (private)      │  migrations → pm-db 5432
          │ (S3-compatible)  │        │ GitLab agent (in the cluster)│  outbound to GitLab only
          └──────────────────┘        └──────────────────────────────┘
```

Everything except the Ingress is on a **private network** (one VPC/VLAN per environment). Staging is a smaller copy
of production on its own network; it may share the monitoring server (the infra project's staging `CLUSTER_LOKI_URL`
points at the same `pm-monitoring` name), never the database.

## 1. Machines

**Procedure.** Ubuntu 24.04 LTS everywhere, unattended security upgrades on, SSH by key only, no public address
except the cluster's load balancer (or the cluster nodes' port 443).

| Machine | Production size (start) | Staging | Runs |
| --- | --- | --- | --- |
| `k3s-1..3` | 3 × 4 vCPU, 8 GB, 80 GB SSD | 1 × 4 vCPU, 8 GB | k3s servers with embedded etcd (survives the loss of one node), Traefik, the workloads |
| `pm-db` | 4 vCPU, 16 GB, 200 GB SSD (+ separate WAL volume) | 2 vCPU, 4 GB | PostgreSQL 18, pgBackRest, server agent |
| `pm-cache` | 2 vCPU, 2 GB | 1 vCPU, 1 GB | Redis 8, server agent |
| `pm-monitoring` | 4 vCPU, 8 GB, 200 GB (logs) | shared | Grafana 12, Loki 3 |
| `gitlab-runner-private` | 2 vCPU, 4 GB | shared | GitLab Runner (Docker executor) for the backend's migration jobs |
| object storage | bucket per environment | bucket | database backups (versioned, object lock if available) |

Sizes follow the measurements of [chapter 6](06-scaling-and-capacity.md): the database is the ceiling (8 API
replicas × (10 pooled + 1 readiness) connections = 88), so it gets the most CPU and memory. Managed services (Amazon RDS / Azure Database
for PostgreSQL, ElastiCache, Grafana Cloud) replace `pm-db`, `pm-cache` and `pm-monitoring` one for one: only the
connection strings in the backend's overlays, the Loki URL of the infra project and the CI/CD variables change.

Each server can run its component two ways: with the infra project's pipeline, where a shell runner with Docker on
the server runs `./infra.sh up <component>` (the same Docker Compose files as on a laptop), or as packages configured
by hand with the same configuration files (the commands below). Both use the repository's `postgresql.conf`,
`roles.sql`, `redis.conf`, `loki.yaml` and Grafana provisioning.

### Private DNS

Create one record per server in the private zone and use only names, never addresses, in the overlays and variables:

| Name | Production | Staging |
| --- | --- | --- |
| database | `pm-db.example.internal` | `pm-db.staging.example.internal` |
| cache | `pm-cache.example.internal` | `pm-cache.staging.example.internal` |
| monitoring | `pm-monitoring.example.internal` | (same) |

The cluster nodes must resolve them (CoreDNS forwards everything outside `cluster.local` to the node's resolver), and
so must the private runner. These are the names in the API's connection strings
(`backend/deploy/k8s/overlays/{staging,production}/api.yaml`), in the backend project's `DB_SERVER` / `REDIS_SERVER`
and in the infra project's `CLUSTER_LOKI_URL`.

### Firewall: who talks to whom

Default deny on every machine; allow exactly this (both host firewall and cloud security groups):

| From | To | Port | Why |
| --- | --- | --- | --- |
| Internet | cluster nodes (or their load balancer) | 443, 80 | HTTPS; port 80 only for the ACME HTTP-01 challenge and the redirect to HTTPS |
| cluster nodes | `pm-db` | 5432 | the API (`pm_app`) |
| private runner | `pm-db` | 5432 | the migration jobs (`pm_migrator`) |
| cluster nodes, private runner | `pm-cache` | 6379 | the API; the migration job invalidates cached lists |
| cluster nodes | `pm-monitoring` | 3100 | Alloy ships every pod's logs to Loki |
| `pm-db`, `pm-cache` | `pm-monitoring` | 3100 | the server agents ship the servers' logs to Loki |
| operators (VPN or SSO proxy) | `pm-monitoring` | 3000 | Grafana |
| operators (VPN / bastion) | all | 22 | administration |
| cluster nodes ↔ cluster nodes | | 6443, 10250, 2379–2380, 8472/udp | k3s API, kubelet, etcd, flannel VXLAN |
| operators (VPN) | cluster nodes | 6443 | `kubectl` for people (CI uses the agent instead) |
| cluster nodes (agent), runners (including the shell runners on the servers) | GitLab | 443 | the agent and the runners connect out; nothing connects in |
| `pm-db` | object storage | 443 | backups |

Nothing else: the database, the cache and Loki are never reachable from the internet, and the cluster's API is not
exposed to GitLab.

## 2. The database server

**Tested here:** `postgresql.conf` and `roles.sql` are what the local server container runs with
(`infra/infra.sh up database`, which applies `roles.sql` on every `up`). **Procedure:** the rest.

With the infra pipeline, the job `servers:<environment>` (`COMPONENT=database`) runs exactly that on `pm-db`, with
`POSTGRES_PASSWORD`, `PM_MIGRATOR_PASSWORD` and `PM_APP_PASSWORD` from the CI/CD variables; add TLS and WAL archiving
to `infra/postgres/postgresql.conf` (or a file next to it) as below. Installed from packages instead:

```bash
# PostgreSQL 18 from the PostgreSQL project's repository (PGDG)
sudo apt install -y postgresql-common && sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
sudo apt install -y postgresql-18 pgbackrest

# The repository's configuration, plus what a real server adds (TLS, WAL archiving)
sudo cp infra/postgres/postgresql.conf /etc/postgresql/18/main/conf.d/10-product-management.conf
sudo tee /etc/postgresql/18/main/conf.d/20-server.conf <<'EOF'
ssl = on
ssl_cert_file = '/etc/postgresql/tls/server.crt'
ssl_key_file = '/etc/postgresql/tls/server.key'
ssl_min_protocol_version = 'TLSv1.3'
archive_mode = on
archive_command = 'pgbackrest --stanza=pm archive-push %p'
EOF
```

Size `shared_buffers` (25 % of RAM) and `effective_cache_size` to the machine; the file's values (768 MB, 2 GB) are
for the 3 GiB local container ([ADR 0022](../adr/0022-database-at-scale.md)).

**Who may connect** (`/etc/postgresql/18/main/pg_hba.conf`): TLS only, password (SCRAM), one line per user and source:

```text
# TYPE   DATABASE            USER         ADDRESS           METHOD
local    all                 postgres                       peer
hostssl  product_management  pm_app       10.0.1.0/24       scram-sha-256   # cluster nodes
hostssl  product_management  pm_migrator  10.0.9.10/32      scram-sha-256   # private runner
```

**Database, users and rights.** Passwords come from your secret store; generate them with `openssl rand -hex 32` (hex
values can be masked by GitLab):

```bash
sudo -u postgres createdb product_management
sudo -u postgres psql -d product_management -c 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements'
sudo -u postgres psql -X -d product_management -v db=product_management \
  -v migrator_password="$PM_MIGRATOR_PASSWORD" -v app_password="$PM_APP_PASSWORD" \
  -f infra/postgres/roles.sql
```

`roles.sql` is idempotent: run it again to rotate a password. `pm_migrator` owns the schema (used only by the pipeline's
migrate job), `pm_app` reads and writes rows with a 30 s statement timeout and cannot change the schema
([ADR 0021](../adr/0021-deployment-layout.md)). The schema itself is created by the first pipeline run.

### Backups: pgBackRest to object storage

Continuous WAL archiving plus scheduled base backups give point-in-time recovery to any second within the retention.

```ini
# /etc/pgbackrest/pgbackrest.conf
[global]
repo1-type=s3
repo1-s3-endpoint=s3.eu-central-1.amazonaws.com
repo1-s3-region=eu-central-1
repo1-s3-bucket=pm-db-backups-production
repo1-path=/pgbackrest
repo1-retention-full=4
repo1-cipher-type=aes-256-cbc
compress-type=zst
process-max=2
# repo1-s3-key / repo1-s3-key-secret / repo1-cipher-pass: from the secret store (or an instance role for the keys)

[pm]
pg1-path=/var/lib/postgresql/18/main
```

```bash
sudo -u postgres pgbackrest --stanza=pm stanza-create
sudo systemctl restart postgresql
sudo -u postgres pgbackrest --stanza=pm check                 # WAL archiving works
sudo -u postgres pgbackrest --stanza=pm --type=full backup    # first full backup

# /etc/cron.d/pgbackrest: weekly full, nightly differential
0 2 * * 0   postgres  pgbackrest --stanza=pm --type=full backup
0 2 * * 1-6 postgres  pgbackrest --stanza=pm --type=diff backup
```

Alert when `pgbackrest info` shows no backup in the last 26 hours or archiving lags (the server agent ships
PostgreSQL's log, where a failing `archive_command` is logged).

**Restore drill, monthly** (a backup is only real once it has been restored): on a scratch machine with the same
PostgreSQL version and pgBackRest configuration,

```bash
sudo systemctl stop postgresql
sudo -u postgres pgbackrest --stanza=pm --delta --type=time --target="2026-10-10 09:00:00+00" \
  --target-action=promote restore
sudo systemctl start postgresql
sudo -u postgres psql -d product_management -c 'SELECT count(*) FROM products'
# and the data invariants: PSQL='sudo -u postgres psql -d product_management' backend/load-tests/verify-data.sh
```

Write down how long it took: that is your recovery time.

## 3. The cache server

**Tested here:** `redis.conf` is the local server's configuration (`infra/infra.sh up redis`). **Procedure:** the
rest. With the infra pipeline, `servers:<environment>` (`COMPONENT=redis`) starts the same container on `pm-cache`
with `REDIS_PASSWORD`; installed from packages:

```bash
# Redis 8 from the Redis project's repository (packages.redis.io)
sudo apt install -y redis
sudo cp infra/redis/redis.conf /etc/redis/redis.conf
sudo tee -a /etc/redis/redis.conf <<EOF
requirepass $REDIS_PASSWORD
# TLS on the private network (instead of plain 6379)
port 0
tls-port 6379
tls-cert-file /etc/redis/tls/server.crt
tls-key-file /etc/redis/tls/server.key
tls-ca-cert-file /etc/redis/tls/ca.crt
tls-auth-clients no
EOF
sudo chmod 640 /etc/redis/redis.conf && sudo systemctl restart redis-server
```

Redis is a pure cache here (no persistence): losing it costs database reads, never data, so it needs no backups. A
replica with Sentinel (or a managed service) removes the restart gap if that matters for latency.

## 4. The monitoring server

**Tested here:** the same Grafana and Loki versions, dashboards and alert rules run locally as the monitoring server
(`infra/infra.sh up logs`, Grafana on `http://localhost:3000`). **Procedure:** the rest.

The observability is **logs only** ([chapter 8](08-observability.md)) and optional: every pod's logs (collected in
the cluster by Alloy, `infra/logs/k8s`, which also receives the browser's errors from the gateway's `/collect`) and
the servers' logs (the server agent, `infra/logs/server-agent.alloy`) go to Loki; Grafana shows them, with the
dashboards and alert rules provisioned from `infra/logs/grafana`.

On `pm-monitoring`, the infra job `servers:<environment>` (`COMPONENT=logs`) runs `./infra.sh up logs`: Loki 3.5
(14 days' retention, `infra/logs/loki.yaml`), Grafana 12.2 with the provisioning, and the agent, as Docker Compose
containers (`infra/logs.yaml`). By hand on the server, from a clone of the infra repository:

```bash
./infra.sh up logs                 # Loki on 3100, Grafana on 3000, the agent; volumes pm-loki-data, pm-grafana-data
```

For production, configure Loki with object storage for its chunks and the retention period the company requires
(`limits_config.retention_period` with the compactor: 14 days in `loki.yaml`, e.g. 30 days or longer for audit), sign
in to Grafana through your identity provider (never the local anonymous admin: replace the `GF_AUTH_*` settings in
`logs.yaml` with your SSO settings, e.g. `GF_AUTH_GENERIC_OAUTH_ENABLED=true` and `GF_SERVER_ROOT_URL`), and send
alert notifications to the on-call channel (contact points in the alerting provisioning). Grafana Cloud can replace
this machine: the agents and the cluster's collector then push to its Loki endpoint with a token
(`LOKI_URL`, `LOKI_USERNAME`, `LOKI_PASSWORD`).

**Server agents.** On `pm-db` and `pm-cache`, run the agent with Loki's URL: with `LOKI_URL` set, `infra.sh up logs`
starts only the agent (no Loki, no Grafana), which ships the logs of the server containers on that machine:

```bash
LOKI_URL=http://pm-monitoring.example.internal:3100/loki/api/v1/push ./infra.sh up logs
```

The infra pipeline's matrix runs `logs` only on the monitoring server's runner, so this is a step to run once per
server by hand (or add a job for it). Where PostgreSQL or Redis are installed from packages instead of containers,
install Grafana Alloy as a service and adapt `infra/logs/server-agent.alloy` to read journald or the services' log
files instead of Docker.

**The cluster's log collector.** The infra job `cluster-logs:<environment>` applies `infra/logs/k8s` through the
GitLab agent, with `LOKI_URL=$CLUSTER_LOKI_URL` (e.g. `http://pm-monitoring.example.internal:3100/loki/api/v1/push`).
Without it, deploy the frontend without a collector (a CI/CD variable `LOGS=off` in the frontend project, which its
`deploy/deploy.sh` reads, or `COLLECTOR_UPSTREAM: ""` in its overlay), or the browser's error reports get a 502.

## 5. The Kubernetes cluster (k3s)

**Tested here:** the manifests render and validate for staging and production
(`backend/deploy/ci/validate-manifests.sh` and `frontend/deploy/ci/validate-manifests.sh`, in each pipeline's test
stage), and the same bases run on Docker Desktop. **Procedure:** k3s itself.

```bash
# First server (pin the version: the manifests are validated against Kubernetes 1.34)
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.34.1+k3s1" K3S_TOKEN="$K3S_TOKEN" \
  sh -s - server --cluster-init --tls-san k3s.example.internal \
  --secrets-encryption --write-kubeconfig-mode 600
# The other two servers
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.34.1+k3s1" K3S_TOKEN="$K3S_TOKEN" \
  sh -s - server --server https://k3s-1.example.internal:6443 --secrets-encryption
```

k3s brings what the manifests need: Traefik as the Ingress controller (`ingressClassName: traefik`), metrics-server
(the two HorizontalPodAutoscalers), and a network policy controller, so the NetworkPolicies (only the gateway reaches
the API and the web app) are enforced. `--secrets-encryption` encrypts Secrets at rest in etcd. Schedule etcd
snapshots off the nodes (`--etcd-s3` flags), although the cluster holds no data that Git and the pipeline cannot
recreate.

**Pulling from the GitLab registry.** The projects' images are private (`registry.gitlab.com/zeiss/backend/api`,
`…/frontend/web`, `…/frontend/gateway`), so the cluster needs a read-only credential. In backend and in frontend,
create a **deploy token named exactly `gitlab-deploy-token`** with the `read_registry` scope (project → Settings →
Repository → Deploy tokens; or once in the group, which serves its immediate child projects). GitLab then passes it
to every job as `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD`, and each deploy job stores it in the namespace as a
`docker-registry` Secret (`gitlab-registry-backend`, `gitlab-registry-frontend`: one per project, so two project
tokens never overwrite each other), which the staging and production overlays reference in `imagePullSecrets`. The
job's own `CI_JOB_TOKEN` would not do: it expires with the job, and a pod rescheduled later could no longer pull. No
node configuration is needed; the local overlay pulls nothing (local images).

**cert-manager and Let's Encrypt** (TLS for the public entry point):

```bash
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.19.1/cert-manager.yaml
kubectl -n cert-manager rollout status deployment/cert-manager-webhook --timeout=180s
kubectl apply -f - <<'EOF'
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: letsencrypt                 # the name the Ingress annotations use
spec:
  acme:
    server: https://acme-v02.api.letsencrypt.org/directory
    email: ops@example.com
    privateKeySecretRef: { name: letsencrypt-account }
    solvers:
      - http01:
          ingress: { ingressClassName: traefik }
EOF
```

Point the public DNS records (`products.example.com`, `staging.products.example.com`) at the nodes or their load
balancer. The first deployment creates the Ingress; cert-manager then obtains the certificate into the Secret
`gateway-tls` and renews it 30 days before expiry. Redirect HTTP to HTTPS in Traefik and add HSTS once the
certificate works.

## 6. TLS inside the private network

**Procedure.** The public certificate stops at Traefik; from there to the gateway and the API traffic stays inside the
cluster. Traffic that leaves the cluster (to PostgreSQL and Redis) crosses the private network and is encrypted:

* **Certificates from an internal CA** (step-ca, Vault PKI, or the cloud's private CA). Issue `pm-db` a certificate
  for `pm-db.example.internal` and `pm-cache` one for `pm-cache.example.internal`: the API's connection strings use
  these names directly, so they are the names a client that verifies the host name checks.
* **Today:** the API's connection strings (`backend/deploy/k8s/overlays/{staging,production}/api.yaml`) use
  `SSL Mode=Require`, and so does the migration job (`DB_SSL_MODE` in `backend/.gitlab-ci.yml`): the connection is
  encrypted, but the server's certificate is not verified, because the API image does not trust the internal CA. For
  Redis, TLS is switched on by appending `,ssl=true,sslHost=pm-cache.example.internal` to the API's Redis connection
  string in the overlay and setting `REDIS_OPTIONS` to the same for the migration job; that does verify the
  certificate, so it also needs the CA trusted.
* **Before go-live (open item):** mount the internal CA into the API pods (a Secret or ConfigMap with `ca.crt`,
  mounted read-only, and `SSL_CERT_FILE` or Npgsql's `Root Certificate` pointing at it), then switch the overlays'
  connection strings and `DB_SSL_MODE` to `VerifyFull`. This is a small change to
  `backend/deploy/k8s/base/deployment.yaml` and the two overlays, not yet made.

## 7. GitLab

**Tested here:** the scripts the pipelines call run locally and in clean containers of the jobs' images, and each
`.gitlab-ci.yml` is validated against GitLab's CI schema; the pipelines run on gitlab.com (test, build and scan on the
shared runners). **Procedure:** the rest of the GitLab configuration; the staging and production jobs have not run
against real clusters.

### Group, projects and branch protection

1. Create a group (here `zeiss`) and three projects in it: `backend` (`backend/`, with `docs/` and the main README),
   `frontend` (`frontend/`) and `deploy` (`infra/`, called infra in this guide). Each part's folder is the root of its
   project, with its own `.gitlab-ci.yml`, `.gitignore` and README.
2. In each project, **protected branch `main`:** allowed to merge: Maintainers; allowed to push: no one; force push
   off.
3. **Merge requests:** pipelines must succeed; all threads resolved; at least one approval (Premium: code owners for
   `deploy/` in backend and frontend, `**/Migrations` in backend, everything in infra).
4. **Container Registry** on in backend (image `api`) and frontend (images `web`, `gateway`). Settings → Packages and
   registries: a **protected/immutable tag rule** for `.*` except `buildcache` (a released tag can never be
   overwritten), and a **cleanup policy** (keep the last 50 tags per image, remove untagged after 14 days; deployed
   tags are within that window).
5. Settings → CI/CD → General pipelines (backend and frontend): **Prevent outdated deployment jobs** on, with **Allow
   job retries for rollback deployments** on.

### Runners

| Project | Jobs | Runner | Why |
| --- | --- | --- | --- |
| backend, frontend | test, build, scan, verify, deploy | gitlab.com shared Linux runners (no tags), or your own: **privileged** Docker executor, "Run untagged jobs", instance runners turned off for the project | `docker:dind` for Testcontainers and image builds; the deploy jobs reach the cluster through the agent, not the network |
| backend | `migrate:*` | **self-hosted runner in the private network**, tag `product-management-private` (`PRIVATE_RUNNER_TAG` in `.gitlab-ci.yml`; override it as a CI/CD variable) | the only place that can reach `pm-db:5432`; the database is never exposed |
| infra | `validate:*`, `cluster-logs:*` | shared runners | no network access needed; the collector is applied through the agent (or a kubeconfig) |
| infra | `servers:*` | **a shell runner with Docker on each server**, tag `pm-<environment>-<component>` (`pm-staging-database`, `pm-production-redis`, `pm-production-logs`, …) | `./infra.sh up <component>` runs on the machine that hosts the component |

Install the private runner for the backend (Docker executor, not privileged: it only runs the API image):

```bash
curl -L "https://packages.gitlab.com/install/repositories/runner/gitlab-runner/script.deb.sh" | sudo bash
sudo apt install -y gitlab-runner docker.io
# backend project → Settings → CI/CD → Runners → New project runner: tag product-management-private,
# "Protected" (runs jobs from protected branches only: the only jobs that hold production secrets), not untagged.
sudo gitlab-runner register --url https://gitlab.com --token "$RUNNER_TOKEN" --executor docker \
  --docker-image alpine:3.22
```

Install a shell runner on each server for the infra project (Docker Engine with the Compose plugin; the runner's
user may use Docker):

```bash
curl -L "https://packages.gitlab.com/install/repositories/runner/gitlab-runner/script.deb.sh" | sudo bash
sudo apt install -y gitlab-runner docker-ce docker-compose-plugin     # Docker's apt repository
sudo usermod -aG docker gitlab-runner
# infra project → Settings → CI/CD → Runners → New project runner: tag pm-production-database (one per machine),
# "Protected", not untagged.
sudo gitlab-runner register --url https://gitlab.com --token "$RUNNER_TOKEN" --executor shell
```

### The GitLab agent for Kubernetes

The deploy jobs use `kubectl` through the agent: a pod in each cluster that connects **out** to GitLab, so the
clusters' API servers are never exposed. One agent per cluster, shared by the three projects:

1. In the infra project, `zeiss/deploy` (or a separate "cluster management" project), add
   `.gitlab/agents/production/config.yaml` (and `staging/config.yaml` for the staging cluster):

   ```yaml
   ci_access:
     projects:
       - id: zeiss/backend
         access_as:
           ci_job: {}    # act as the CI job's identity: grant it rights with RBAC (below)
       - id: zeiss/frontend
         access_as:
           ci_job: {}
       - id: zeiss/deploy
         access_as:
           ci_job: {}
   ```

2. Operate → Kubernetes clusters → Connect a cluster → name `production`; GitLab shows a `helm upgrade --install`
   command with a token. Run it against the cluster (from an operator's machine over the VPN).
3. RBAC for the jobs: with `access_as: ci_job`, the job's identity is the group `gitlab:ci_job` plus
   `gitlab:project_env:<project id>:<environment>`. Bind each project to what its deploy script applies:

   | Project | Applies |
   | --- | --- |
   | backend | the namespace `product-management`; in it the Deployment, Service, HPA, PDB and NetworkPolicy `api*`, and the Secrets `api-secrets` and `gitlab-registry-backend` |
   | frontend | the namespace `product-management`; in it the `web` and `gateway` Deployments, Services, HPA, PDBs, NetworkPolicy, the Ingress and the Secret `gitlab-registry-frontend` |
   | infra | the namespace `observability` and everything in it (Alloy, its ConfigMap, Service, NetworkPolicy, the Secret `loki-credentials`), plus the ClusterRole/ClusterRoleBinding `observability-alloy` (read-only access to pods and their logs) |

   The simplest correct start is `cluster-admin` bound to `gitlab:project_env:<project id>:production` for each of the
   three projects; tighten it to a Role per namespace (and the one ClusterRole for infra) afterwards.
4. The jobs select the cluster with `KUBE_CONTEXT=<agent configuration project path>:<agent name>`, e.g.
   `zeiss/deploy:production`.

Without the agent, a **kubeconfig** works too: a File-type variable `KUBECONFIG`, scoped to the environment, whose
current context is the cluster. The deploy scripts (`deploy/ci/deploy-environment.sh` in backend and frontend,
`ci/cluster-logs.sh` in infra) use `KUBE_CONTEXT` when it is set (GitLab also sets a `KUBECONFIG` with the agent's
contexts in every job of a project the agent is shared with), else the kubeconfig's current context, else fail with
"set KUBE_CONTEXT (GitLab agent) or a KUBECONFIG file variable". A kubeconfig means the runner reaches the cluster's
API (port 6443): a self-hosted runner next to the cluster, not the shared runners (see "One machine of our own" below).

### Environments

Operate → Environments: `staging` and `production` are created by each project's first pipeline. Then, in each
project, Settings → CI/CD → **Protected environments**: protect `production`, allowed to deploy: Maintainers (or a
"release managers" group), **required approvals: 1** (Premium). Protect `staging` as well (allowed to deploy:
Developers + Maintainers) so its protected variables are only exposed to jobs on `main`.

Deployments of the same environment must never overlap and must run oldest first, in each project:

```bash
for project in "$BACKEND_PROJECT_ID" "$FRONTEND_PROJECT_ID"; do
  for environment in staging production; do
    curl --request PUT --header "PRIVATE-TOKEN: $GITLAB_TOKEN" \
      "https://gitlab.com/api/v4/projects/$project/resource_groups/$environment?process_mode=oldest_first"
  done
done
```

### CI/CD variables

Settings → CI/CD → Variables, in the group or in a project. **Protected** = only exposed to pipelines on protected
branches (`main`); **Masked and hidden** = never shown in logs nor in the UI after saving; **File** = GitLab writes the
value to a temporary file and the variable holds its path. Environment scope `staging` / `production` = one value per
environment, picked by the job's `environment:`.

**Switches** (group or each project, scope **all**: the pipelines' `rules` decide with them which jobs exist, and rules
cannot see environment-scoped variables). Until they are set, every pipeline stops after test, build and scan and is
green; nothing tries to reach a cluster that does not exist yet.

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `STAGING_ENABLED` | Variable | | | all | `true`: pipelines on `main` deploy staging (backend: migrate, deploy, verify; frontend: deploy, E2E; infra: its manual staging jobs) |
| `PRODUCTION_ENABLED` | Variable | | | all | `true`: the manual production jobs appear (backend and frontend also need `STAGING_ENABLED`: production deploys what staging verified) |
| `PRIVATE_RUNNER_TAG` | Variable | | | all | only to change the self-hosted runner's tag (default `product-management-private`) |

**Deploy token** (not a variable): `gitlab-deploy-token`, scope `read_registry`, in backend and frontend (or once in the
group). GitLab exposes it as `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD`; the deploy jobs fail with a clear message without
it (section 5, "Pulling from the GitLab registry").

**Group level** (shared by infra and backend, which must use the same values: infra sets them on the servers, the
backend gives them to the migration and the API):

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `PM_MIGRATOR_PASSWORD` | Variable | ✔ | ✔ (hidden) | staging, production | `openssl rand -hex 32`: `pm_migrator`, the schema owner |
| `PM_APP_PASSWORD` | Variable | ✔ | ✔ (hidden) | staging, production | `openssl rand -hex 32`: `pm_app`, the API's database user |
| `REDIS_PASSWORD` | Variable | ✔ | ✔ (hidden) | staging, production | `openssl rand -hex 32`: the Redis password |
| `KUBE_CONTEXT` | Variable | ✔ | | staging, production | `zeiss/deploy:<agent>`; the same agent for the three projects (or set it in each project) |
| `KUBECONFIG` | **File** | ✔ | | staging, production | instead of `KUBE_CONTEXT`: a kubeconfig whose current context is the cluster (self-hosted runner next to it) |

Environment-scoped group variables need GitLab Premium; on the Free tier, set the three passwords in both the infra
and the backend project, with the same values.

**infra:**

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `POSTGRES_PASSWORD` | Variable | ✔ | ✔ (hidden) | staging, production | `openssl rand -hex 32`: the database administrator (people only) |
| `CLUSTER_LOKI_URL` | Variable | ✔ | | staging, production | `http://pm-monitoring.example.internal:3100/loki/api/v1/push`, or a hosted Loki's push URL |
| `LOKI_USERNAME`, `LOKI_PASSWORD` | Variable | ✔ | ✔ (password) | staging, production | only for a hosted Loki (e.g. Grafana Cloud) |

**backend:**

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `DB_SERVER` | Variable | ✔ | | staging, production | `pm-db.staging.example.internal` / `pm-db.example.internal` (the migration jobs) |
| `REDIS_SERVER` | Variable | ✔ | | staging, production | `pm-cache.staging.example.internal` / `pm-cache.example.internal` (the migration jobs) |
| `API_SIGNING_KEY` | **File** | ✔ | | staging, production | `docker run --rm $API_IMAGE generate-signing-key` (a different key per environment) |
| `API_CONSUMERS` | **File** | ✔ | | staging, production | `consumers.json` (below) |
| `DB_SSL_MODE` | Variable | | | all (default in `.gitlab-ci.yml`: `Require`) | `VerifyFull` once the CA is mounted (section 6) |
| `REDIS_OPTIONS` | Variable | | | staging, production | `,ssl=true,sslHost=pm-cache.example.internal` with Redis TLS |
| `STAGING_URL` | Variable | | | all | the public staging URL (default in `.gitlab-ci.yml` is the example name) |
| `CHECK_CLIENT_ID` | Variable | ✔ | | staging | a consumer with the manager role, e.g. `manager` |
| `CHECK_CLIENT_SECRET` | Variable | ✔ | ✔ (hidden) | staging | its secret |

**frontend:**

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `STAGING_URL`, `PRODUCTION_URL` | Variable | | | all | the public URLs (defaults in `.gitlab-ci.yml` are the example names) |
| `E2E_VIEWER_SECRET`, `E2E_CLERK_SECRET`, `E2E_MANAGER_SECRET` | Variable | ✔ | ✔ (hidden) | staging | secrets of the consumers `viewer`, `clerk`, `manager` in staging's API |
| `LOGS` | Variable | | | staging, production | `off` only when the environment has no log collector |

The registry credentials (`CI_REGISTRY_*`) are predefined. Masking requires values of at least 8 characters from the
Base64 alphabet; that is why the passwords are hex. No connection string holds a password: the API's connection
strings are in the backend's overlays, and the passwords reach the API separately, through the Secret `api-secrets`
(`PGPASSWORD`, `Redis__Password`).

**The consumers file.** Only PBKDF2 hashes go in it; the secrets themselves go to the consumers (and to the frontend's
`E2E_*` variables for staging). For each consumer:

```bash
secret="$(openssl rand -hex 24)"                              # give this to the consumer, once
docker run --rm "$API_IMAGE" hash-secret "$secret"           # put this in SecretHash
```

```json
{
  "Authentication": {
    "Consumers": [
      { "ClientId": "manager", "SecretHash": "pbkdf2-sha256$100000$...", "Roles": ["manager"] },
      { "ClientId": "clerk",   "SecretHash": "pbkdf2-sha256$100000$...", "Roles": ["stock-clerk"] },
      { "ClientId": "viewer",  "SecretHash": "pbkdf2-sha256$100000$...", "Roles": ["viewer"] }
    ]
  }
}
```

Staging needs `viewer`, `clerk` and `manager` (the frontend's Playwright suite signs in as each, and the backend's
`check-requirements.py` as a manager). Production has only real consumers. Never reuse the local demo hashes
generated by `backend/deploy/local-secrets.sh`.

### What the pipelines do with them

| Project | Stages | Deploys |
| --- | --- | --- |
| infra | validate (Compose files, `logs/k8s` with kubeconform, `alloy fmt`) | manual only: `servers:<env>` (matrix `database`, `redis`, `logs` on the servers' shell runners) and `cluster-logs:<env>` (the collector, through the agent) |
| backend | test (`test:backend`: format, build, 381 tests on dind; `test:manifests`; SAST, secret detection) → build (`build:api`, `migrations:sql`) → scan (`scan:api`) | `migrate:staging` (private runner, sample data on an empty database) → `deploy:staging` (Secrets `gitlab-registry-backend` and `api-secrets`, then `deploy/deploy.sh`) → `verify:requirements` → `migrate:production` (**manual**) → `deploy:production` |
| frontend | test (`test:frontend`: lint, format, 185 tests, build; `test:manifests`; SAST, secret detection) → build (`build:web`, `build:gateway`) → scan (`scan:web`, `scan:gateway`) | `deploy:staging` (Secret `gitlab-registry-frontend`, then `deploy/deploy.sh`) → `verify:e2e` (Playwright, desktop and phone, against `STAGING_URL`) → `deploy:production` (**manual**) |

Merge request pipelines test, build and scan; pipelines on `main` also deploy, once `STAGING_ENABLED` /
`PRODUCTION_ENABLED` are `true`. Details per job:
[chapter 9](09-deployment-and-operations.md#cicd-pipelines).

### One machine of our own

The short path to a running staging without cloud servers or the agent (**procedure**): k3s, the servers and a GitLab
Runner on one Linux machine, the jobs reaching the cluster with a kubeconfig.

1. **k3s**, one server: `curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.34.1+k3s1" sh -s - --write-kubeconfig-mode 600`.
2. **Servers:** from the infra folder, `POSTGRES_PASSWORD=… PM_MIGRATOR_PASSWORD=… PM_APP_PASSWORD=… REDIS_PASSWORD=… ./infra.sh up`
   (or register a shell runner for the infra project with the tags `pm-staging-database`, `pm-staging-redis`,
   `pm-staging-logs` and run `servers:staging`). In `backend/deploy/k8s/overlays/staging/api.yaml` point the
   connection strings at the machine's address (the pods reach it on its LAN IP).
3. **Runner:** install GitLab Runner and Docker (section 7, "Runners"), register it for the group (or for backend
   and frontend) with the Docker executor, `--docker-privileged` (docker:dind), the tag `product-management-private`
   and "Run untagged jobs" on; turn off instance runners in backend and frontend, so every job, deploys included, runs
   here.
4. **Cluster access:** in backend, frontend and infra a File variable `KUBECONFIG`, scope `staging`, with the content
   of `/etc/rancher/k3s/k3s.yaml` where `server: https://127.0.0.1:6443` is replaced by the machine's LAN address
   (inside a job's container, 127.0.0.1 is the container). No agent, and the cluster's API is not exposed beyond
   the machine's network.
5. **The rest of section 7:** the deploy tokens, the staging variables (`DB_SERVER` / `REDIS_SERVER` = the machine's
   address, the passwords, the signing key, the consumers), the staging Ingress host and `STAGING_URL` (a name that
   resolves to the machine; cert-manager as in section 5 for a certificate the verify jobs accept), then
   `STAGING_ENABLED=true`. The next pipeline on `main` migrates, deploys and verifies staging.

The k6 smoke test is not in the GitLab pipelines: its scripts sign in with the local demo secrets, so it would need
those consumers in staging. Run it by hand after a local deployment (`backend/load-tests/run.sh smoke`).

## 8. The first deployment, in order

1. **Network and DNS:** private network, the private DNS names, the firewall rules (section 1).
2. **GitLab:** the group and the three projects, protections, registry rules, the deploy tokens
   (`gitlab-deploy-token`), the runners (private runner for the backend, shell runners on the servers for infra), the
   agents and their RBAC, protected environments, resource groups, every variable of section 7 (the switches
   `STAGING_ENABLED` / `PRODUCTION_ENABLED` last, once the cluster and the servers are up).
3. **Database server:** PostgreSQL (infra job `servers:<env>` with `database`, or the packages of section 2), TLS,
   `pg_hba.conf`, `roles.sql`, pgBackRest with a first full backup.
4. **Cache server:** Redis with its password, and TLS (`servers:<env>` with `redis`, or section 3).
5. **Monitoring server:** Loki and Grafana (`servers:<env>` with `logs`); the server agents on `pm-db` and `pm-cache`
   (section 4); check that their logs appear.
6. **Cluster:** k3s, cert-manager and the `letsencrypt` ClusterIssuer, public DNS records; then the infra job `cluster-logs:<env>`.
7. **Repositories:** replace the `example.*` names in `backend/deploy/k8s/overlays/{staging,production}/api.yaml`
   (connection strings) and `frontend/deploy/k8s/overlays/{staging,production}/ingress.yaml` (Ingress hosts), and set
   `STAGING_URL` / `PRODUCTION_URL`; open a merge request in each. Their pipelines test, build and scan; read
   `migrations.sql` in the backend's MR.
8. **Switch staging on** (`STAGING_ENABLED=true`) and **merge the backend.** The pipeline on `main` migrates and
   deploys staging, then verifies it.
9. **Merge the frontend.** Its pipeline deploys the web app and the gateway to staging and runs Playwright against
   it. Watch the first certificate being issued: `kubectl -n product-management get certificate,ingress`.
10. **Production:** set `PRODUCTION_ENABLED=true`; in the backend's pipeline start `migrate:production`; approve the deployment if approvals are
    required; `deploy:production` rolls out the API. Then the frontend's `deploy:production`.
11. **Check by hand:** sign in on `https://products.example.com`, look at the API and gateway logs in Grafana, run
    `BASE_URL=https://products.example.com CHECK_CLIENT_ID=... CHECK_CLIENT_SECRET=... python3 backend/scripts/check-requirements.py`
    once with a production manager consumer (it creates and deletes its own products).

## 9. Day 2

**Procedure** for everything in this section, except where marked.

### Roll back a release

In the backend or the frontend project: Operate → Environments → production → the previous successful deployment →
**Re-deploy** (Rollback). GitLab re-runs that pipeline's `deploy:production`: its images, its manifests and, for the
API, the current Secret. The database is not rolled back: every migration is backward compatible with the release
before it (expand/contract), and a bad migration is fixed by a new one. In an emergency,
`kubectl -n product-management rollout undo deployment/api` (and `gateway`, `web`) is faster; the next pipeline
deployment reconciles it (**tested here** locally).

### Rotate secrets

The API's secrets are rotated by changing a variable and re-running the backend's last deploy job:
`create-secrets.sh` updates `api-secrets`, reports that it changed, and the job restarts the API so the pods read it.

* **Signing key:** generate a new key, replace the `API_SIGNING_KEY` file variable, re-run the backend's deploy job.
  Tokens signed with the old key are refused: every consumer signs in again (tokens live one hour). Do it at a quiet
  time.
* **Database password of `pm_app`:** PostgreSQL checks passwords only when a connection opens, so open connections
  survive. Update the group variable `PM_APP_PASSWORD`, then, close together, apply `roles.sql` on the server with the
  new value (the infra job `servers:<env>` for `database`, which runs it on every `up`, or `psql … -f roles.sql` by
  hand) and re-run the backend's deploy job (which restarts the API with the new `PGPASSWORD`). New connections from
  not-yet-restarted pods fail for the minute between the two; for zero impact, alternate between two app users
  (`pm_app_blue` / `pm_app_green`).
* **`pm_migrator`:** update `PM_MIGRATOR_PASSWORD` and apply `roles.sql` (the infra job); only the migrate jobs use it.
* **Redis:** with the infra's container, update `REDIS_PASSWORD`, re-run `servers:<env>` for `redis` (the container is
  recreated with the new password: the cache starts empty, and the API fails to reach Redis until the next step), then
  re-run the backend's deploy job. Without that gap: add the new password next to the old one
  (`ACL SETUSER default on >new-password`), update the variable, re-run the backend's deploy job, then remove the old
  one (`ACL SETUSER default <old-password`) and re-run the infra job.
* **Consumers:** edit `API_CONSUMERS`, re-run the backend's deploy job (the API reads the file at startup, and the job
  restarts it).
* **Hosted Loki token:** update `LOKI_PASSWORD`, re-run `cluster-logs:<env>`, then
  `kubectl -n observability rollout restart deployment/alloy`.

### Scale

The HorizontalPodAutoscalers own the replica counts (API 2–8, gateway 2–6). To change the range, edit
`backend/deploy/k8s/base/hpa.yaml` (or the gateway's HPA in `frontend/deploy/k8s/base/gateway.yaml`, or patch either in
an overlay) and deploy: keep `maxReplicas × 11` connections within what PostgreSQL can serve
([chapter 6](06-scaling-and-capacity.md)). More nodes: join k3s agents
(`curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION=... K3S_URL=https://k3s-1.example.internal:6443 K3S_TOKEN=... sh -`).
A bigger database is a bigger `pm-db` (or a read replica for lists and searches, which needs a code change).

### Upgrade

* **k3s:** one node at a time: `kubectl drain <node> --ignore-daemonsets --delete-emptydir-data`, re-run the installer
  with the next patch or minor `INSTALL_K3S_VERSION`, `kubectl uncordon <node>`. The PodDisruptionBudgets keep an API,
  gateway and web pod up throughout. Never skip a minor version; bump the `KUBE_TOOLS_IMAGE` kubectl in the three
  pipelines and the `-kubernetes-version` of both `validate-manifests.sh` scripts (and of infra's
  `validate:logs-k8s`) with it.
* **PostgreSQL minor:** a new image (`servers:<env>` pulls `postgres:18-alpine` again) or `apt upgrade`, and a restart
  (seconds of connection errors; the API retries reads, and the readiness probe tolerates 30 s). **Major:**
  `pg_upgrade --link` on a copy restored from backup first, then for real in a maintenance window.
* **Images and tools:** the pinned versions are at the top of each project's `.gitlab-ci.yml` and in the infra's
  Compose files; base images in the Dockerfiles are rebuilt on every pipeline (`--pull`), and Trivy fails the pipeline
  when a critical fix is available.

### Restore the database

For data loss or corruption, restore to a point in time just before the incident (section 2's drill, on the real
server or a new one), then:

1. Stop writes: remove the autoscaler (its minimum cannot be 0 with CPU metrics) and scale the API to zero:
   `kubectl -n product-management delete hpa api && kubectl -n product-management scale deployment/api --replicas=0`.
   The gateway keeps serving the web app and answers API calls with 502 meanwhile.
2. Restore with pgBackRest (`--type=time --target=...`), start PostgreSQL, run `backend/load-tests/verify-data.sh`
   with `PSQL=...` against it.
3. Invalidate the cache: re-run the backend's `migrate` job (it moves the cache generations on), or restart Redis.
4. Re-run the backend's last deploy job: it recreates the HPA and scales the API back.

If `pm-db` itself is lost, restore onto a new server and point the private DNS name at it; nothing in the cluster
changes.

### Read the logs

Grafana → Explore → Loki: `{namespace="product-management", app="api"}` for the API, `{app="gateway"}` for the access
log; the servers' logs under `{namespace="servers"}` ([chapter 8](08-observability.md)). Without Grafana:
`kubectl -n product-management logs deployment/api`.

## 10. Production checklist

| | Item |
| :---: | --- |
| ☐ | Private network; firewall rules exactly as in section 1; no public address on the servers |
| ☐ | PostgreSQL: TLS on, `hostssl` only, `roles.sql` applied, `pg_stat_statements`, sized `shared_buffers` |
| ☐ | Backups: WAL archiving healthy, a full backup exists, a restore drill done and timed, alert on missing backups |
| ☐ | Redis: password, TLS, `maxmemory` sized, dangerous commands renamed (in `redis.conf`) |
| ☐ | Monitoring: Loki retention, Grafana behind SSO, alerts routed to on-call, server agents shipping, the cluster's collector deployed |
| ☐ | Cluster: 3 k3s servers, Secrets encrypted at rest, metrics-server running (`kubectl top nodes`) |
| ☐ | TLS: certificate issued for the public names, HTTP redirects to HTTPS, HSTS |
| ☐ | Internal CA trusted by the API, `SSL Mode=VerifyFull` in the overlays and `DB_SSL_MODE=VerifyFull` (open item, section 6) |
| ☐ | GitLab: three projects, `main` protected, MR approvals, immutable image tags, cleanup policy, deploy token `gitlab-deploy-token` (`read_registry`) in backend and frontend, protected private runner, protected shell runners on the servers |
| ☐ | Agent: one per cluster, RBAC limited to what each project's deploy script applies |
| ☐ | Environments: `production` protected with required approval; resource groups `oldest_first` |
| ☐ | Variables: all of section 7, protected and masked as listed, `STAGING_ENABLED` / `PRODUCTION_ENABLED` set, the shared passwords identical in infra and backend, a new signing key per environment, no local demo values |
| ☐ | `example.*` names replaced in the backend's and the frontend's overlays and in `STAGING_URL` / `PRODUCTION_URL` |
| ☐ | A full pipeline run on `main` green through staging in backend and frontend; one production deployment and one rollback rehearsed |
| ☐ | The HPAs' limits re-measured on the real nodes ([chapter 6](06-scaling-and-capacity.md)) |
| ☐ | Runbooks for this chapter's day-2 procedures linked from the alerts |

## Where to look

* Pipelines: [`infra/.gitlab-ci.yml`](../../infra/.gitlab-ci.yml), [`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml),
  [`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml)
* Pipeline scripts: [`backend/deploy/ci/create-secrets.sh`](../../backend/deploy/ci/create-secrets.sh),
  [`backend/deploy/ci/deploy-environment.sh`](../../backend/deploy/ci/deploy-environment.sh),
  [`backend/deploy/ci/validate-manifests.sh`](../../backend/deploy/ci/validate-manifests.sh),
  [`backend/deploy/deploy.sh`](../../backend/deploy/deploy.sh), [`frontend/deploy/deploy.sh`](../../frontend/deploy/deploy.sh),
  [`frontend/deploy/ci/deploy-environment.sh`](../../frontend/deploy/ci/deploy-environment.sh),
  [`frontend/deploy/ci/validate-manifests.sh`](../../frontend/deploy/ci/validate-manifests.sh),
  [`infra/ci/cluster-logs.sh`](../../infra/ci/cluster-logs.sh)
* Overlays: [`backend/deploy/k8s/overlays/staging`](../../backend/deploy/k8s/overlays/staging),
  [`backend/deploy/k8s/overlays/production`](../../backend/deploy/k8s/overlays/production),
  [`frontend/deploy/k8s/overlays/staging`](../../frontend/deploy/k8s/overlays/staging),
  [`frontend/deploy/k8s/overlays/production`](../../frontend/deploy/k8s/overlays/production)
* Servers: [`infra/`](../../infra) ([`infra.sh`](../../infra/infra.sh), `database.yaml`, `redis.yaml`, `logs.yaml`,
  `postgres/postgresql.conf`, `postgres/roles.sql`, `redis/redis.conf`, `logs/`) and its [README](../../infra/README.md)
* Decision: [ADR 0021 deployment layout](../adr/0021-deployment-layout.md)

## How to verify

What can be checked without real servers (**tested here**):

```bash
# Every overlay renders and is valid; the settings each overlay must set; no Secret in staging/production; staging
# and production pull with the registry Secret
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 backend/deploy/ci/validate-manifests.sh
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 frontend/deploy/ci/validate-manifests.sh

# What staging and production would receive
kubectl kustomize backend/deploy/k8s/overlays/production | less
kubectl kustomize frontend/deploy/k8s/overlays/production | less

# The Secret the backend's deploy job would create, validated by the local cluster without changing it
DRY_RUN=server PM_APP_PASSWORD=$(openssl rand -hex 32) REDIS_PASSWORD=$(openssl rand -hex 32) \
  API_SIGNING_KEY=<a PEM file> API_CONSUMERS=<a consumers.json> backend/deploy/ci/create-secrets.sh

# The infra: every Compose file parses, the collector renders
docker compose -f infra/database.yaml config --quiet && docker compose -f infra/redis.yaml config --quiet \
  && docker compose -f infra/logs.yaml --profile storage config --quiet
kubectl kustomize infra/logs/k8s | less

# The SQL the migrations run (the pipeline's review artifact)
cd backend && dotnet tool restore && dotnet restore ProductManagement.sln && dotnet ef migrations script --idempotent \
  --project ProductManagement.Infrastructure --startup-project ProductManagement.Api --output ../migrations.sql

# Each pipeline's structure (resolves GitLab's templates), from inside each part
cd backend && npx gitlab-ci-local@4 --list
```
