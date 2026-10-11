# 12. From zero to production

**After this chapter you will know** how to take this repository from an empty GitLab group and a set of fresh
machines to a running staging and production: how the machines become one k3s cluster with nodes for the database,
the cache and the services, which ports they need between them, how TLS is set up at the edge and what stays inside
the cluster's network, how backups and restores work, how GitLab is configured (one project per repository, runners,
the agent for Kubernetes, environments, the tokens, every CI/CD variable per project), the order of the first
deployment, the day-2
procedures (rotating secrets, scaling, upgrading, restoring, rolling back), and a checklist for go-live.

The system is three GitLab projects in one group: **backend** (the API), **frontend** (the web app and the gateway)
and **deploy** (every environment: the apps' manifests, `environments/<env>` with the versions it runs, the shared
infrastructure in the cluster's namespace `infra`, and the scripts). Each has its own `.gitlab-ci.yml`; backend and
frontend have the registries. Only the deploy project's pipeline applies anything to a cluster. Releases are pulled:
the app pipelines only publish versions (a tag `v1.4.0` publishes `api:1.4.0`), and the deploy project moves staging
to the newest versions on a schedule and production by merge request (GitOps,
[chapter 9](09-deployment-and-operations.md#the-layout-three-parts)).

It is a runbook: commands to copy, in order. It is also honest about what has been exercised. Each section is marked:

* **Tested here:** run in this repository, locally or in CI (Docker Desktop's Kubernetes runs the same manifests and
  scripts as a production cluster, and so does a single-node k3s on a fresh Ubuntu 24.04 VM, prepared with
  `deploy/scripts/setup-ubuntu.sh`).
* **Procedure:** the standard way to do it, written for this system but not executed against real machines as part
  of this assessment. Versions and flags are current at the time of writing; check them against the vendors' docs.

All names below (`example.com`, `example.internal`, `10.0.x.x`) are placeholders.

---

## The target

```text
                       Internet
                          │ 443 (HTTPS)
  ┌───────────────────────▼──────────────────────────────────────────────────────┐
  │ k3s cluster, private network                                                 │
  │                                                                              │
  │  k3s-1..3   servers: control plane (embedded etcd), Traefik (Ingress, TLS by │
  │             cert-manager); product-management: gateway → API / web;          │
  │             infra: Loki, Grafana, Alloy (every pod's logs)                   │
  │  pm-db      node infra/role=database, tainted: PostgreSQL + nightly pg_dump  │
  │  pm-cache   node infra/role=cache, tainted: Redis                            │
  │  apps-N     nodes infra/role=apps (optional): more room for the services     │
  └──────────┬──────────────────────────────────────────────┬────────────────────┘
             │ dumps copied off the node                    │ outbound only
  ┌──────────▼─────────┐                    ┌───────────────▼──────────────────────┐
  │ object storage     │                    │ GitLab: the agent connects out; jobs │
  │ (S3-compatible)    │                    │ on the shared runners use kubectl    │
  └────────────────────┘                    └──────────────────────────────────────┘
```

Everything runs in Kubernetes: nothing is installed by hand for the database, the cache or the logs. The machines
are k3s nodes; the dedicated ones carry a label and a taint (`infra/role`) so that PostgreSQL and Redis get them to
themselves. Everything except the Ingress is on a **private network** (one VPC/VLAN per environment). Staging is a
smaller copy of production in its own cluster, often a single machine; it never shares the database with production.

## 1. Machines

**Procedure.** Ubuntu 24.04 LTS everywhere, unattended security upgrades on, SSH by key only, no public address
except the cluster's load balancer (or the nodes' port 443).

| Machine | Production size (start) | Staging | Runs |
| --- | --- | --- | --- |
| `k3s-1..3` | 3 × 4 vCPU, 8 GB, 80 GB SSD | 1 × 4 vCPU, 8 GB (everything below on it) | k3s servers with embedded etcd (survives the loss of one), Traefik, the services' pods, Loki, Grafana, Alloy |
| `pm-db` (role `database`) | 4 vCPU, 16 GB, 200 GB SSD | — | PostgreSQL and its nightly dump, nothing else (taint) |
| `pm-cache` (role `cache`, optional) | 2 vCPU, 2 GB | — | Redis, nothing else (taint); without it Redis runs on any node |
| `apps-N` (role `apps`, optional) | as measured | — | more room for the services' pods |
| object storage | bucket per environment | bucket | copies of the nightly dumps (versioned, object lock if available) |

Sizes follow the measurements of [chapter 6](06-scaling-and-capacity.md): the database is the ceiling (8 API
replicas × (10 pooled + 1 readiness) connections = 88), so it gets the most CPU and memory. Managed services (Amazon
RDS / Azure Database for PostgreSQL, ElastiCache, Grafana Cloud) can replace the in-cluster PostgreSQL, Redis and
Loki: the connection strings in `deploy/environments/<env>/api.yaml` name another host (with TLS), or the deploy
project's `LOKI_URL` another Loki.

A single machine is a complete cluster too: one k3s server runs everything, and `deploy/scripts/add-node.sh` adds dedicated
nodes when they are needed (section 5). The roles are a preference plus a taint, not a requirement: without a
`database` node PostgreSQL simply runs on a server.

### Private DNS

One record per machine in the private zone, and one for the cluster's API (`k3s.example.internal`, in the servers'
certificate with `--tls-san`, section 5). The services need no DNS of their own: they reach PostgreSQL, Redis and
Loki by their cluster names (`postgres.infra.svc.cluster.local`, `redis.infra.svc.cluster.local`,
`loki.infra.svc.cluster.local`), the same in every environment, so the environments' `api.yaml` need no per-environment
host names.

### Firewall: who talks to whom

Default deny on every machine; allow exactly this (both host firewall and cloud security groups):

| From | To | Port | Why |
| --- | --- | --- | --- |
| Internet | the nodes running Traefik (or their load balancer) | 443, 80 | HTTPS; port 80 only for the ACME HTTP-01 challenge and the redirect to HTTPS |
| all nodes | k3s servers | 6443 | the k3s API: nodes join and talk to the control plane |
| k3s servers ↔ k3s servers | | 2379–2380 | etcd (the three-server control plane) |
| all nodes ↔ all nodes | | 10250 | kubelet (logs, `exec`, metrics) |
| all nodes ↔ all nodes | | 8472/udp | flannel VXLAN: the pod network, which also carries the traffic to PostgreSQL, Redis and Loki |
| operators (VPN) | k3s servers | 6443 | `kubectl` for people, `create-database.sh` (CI uses the agent instead) |
| operators (VPN / bastion) | all nodes | 22 | administration, and `add-node.sh` (ssh to the new node and to a server) |
| all nodes | GitLab, image registries | 443 | the agent connects out, the nodes pull images; nothing connects in |
| `pm-db` | object storage | 443 | the dumps copied off the node (section 2) |

Nothing else. PostgreSQL (5432), Redis (6379), Loki (3100) and Grafana (3000) are Services inside the cluster with
no port on any machine: no runner and no machine outside the cluster connects to them, and people reach Grafana and
the database through `kubectl port-forward` / `kubectl exec`. The cluster's API is not exposed to GitLab.

## 2. The database node

**Tested here:** [`postgres.yaml`](../../deploy/infra/postgres.yaml) and [`create-database.sh`](../../deploy/scripts/lib/create-database.sh)
run on Docker Desktop (`deploy/scripts/infra.sh up postgres`, then `create-database.sh`); `add-node.sh --dry-run` prints its
steps. **Procedure:** a real database node.

From an operator's machine with ssh (and sudo) to the new machine and to a k3s server, and `kubectl` with the cluster
as its context:

```bash
deploy/scripts/add-node.sh admin@pm-db.example.internal database --server https://k3s.example.internal:6443
kubectl get nodes -L infra/role
```

[`add-node.sh`](../../deploy/scripts/add-node.sh) reads the join token from the server
(`/var/lib/rancher/k3s/server/node-token`, over ssh; or `--token-file`), installs the k3s agent (`v1.34.1+k3s1`)
with the label `infra/role=database` and the taint `infra/role=database:NoSchedule` from its first moment, waits for
the node to be Ready and applies both again with `kubectl`. PostgreSQL and its backup job prefer that label and
tolerate the taint; nothing else does, so nothing else lands there.

**Add the database node before `infra.sh up postgres`.** The data volume (k3s's `local-path` storage: a directory
on the node's disk) is created on the node where the pod first starts, and it never moves: the pod follows its
volume. A PostgreSQL started before the node existed stays where it is; moving it is a dump and restore
(section 9, "Restore the database"). After the first `up`, check where it runs:
`kubectl -n infra get pod postgres-0 -o wide`.

**Size it to the node.** `postgres.yaml` is sized for 4 CPUs and 3 GiB (the StatefulSet's limits and the
`postgresql.conf` in the ConfigMap `postgres-config`). On a dedicated node raise the limits and scale
`shared_buffers` (~25 % of the RAM) and `effective_cache_size` (~65–75 %), then `deploy/scripts/infra.sh up postgres` (or the
deploy project's `infra:<env>` job) and the restart it prints ([ADR 0022](../adr/0022-database-at-scale.md) for the measurements behind the values). The volume
requests 20 GiB, but `local-path` does not enforce sizes: the node's disk is the limit, so give the node a disk for the
data and the dumps, and watch its free space (the alert rule on PostgreSQL's errors includes "No space left on
device").

**Databases, users and rights.** Nothing to run: the first `deploy:<environment>` (or `deploy/scripts/deploy.sh
<environment>`) finds no Secret `product-management/database-credentials` and runs `deploy/scripts/lib/create-database.sh
product-management product_management pm_app pm_migrator` itself. That creates the database and the users `pm_migrator`
(owns the schema; the API's migration step at startup connects as it) and `pm_app` (the API's requests: a 30 s statement
timeout, no schema rights) with random passwords, kept only in that Secret; no password is ever in GitLab. The first API
pod's migrations then create the tables, and `pm_app`'s rights on them come from the migration `GrantAppPermissions`
([chapter 9](09-deployment-and-operations.md#deployscriptslibcreate-databasesh)). Run the script by hand only for another
service's database, after restoring a dump, or with `--rotate`.

**Who may connect.** Anyone with a valid password who reaches the Service, which has no address outside the cluster.
To also limit which namespaces may open connections, add a NetworkPolicy in `infra` (k3s enforces them); there is none
in the repository yet.

### Backups: nightly dumps, copied off the node

**Tested here:** the CronJob in `postgres.yaml`, run once by hand on Docker Desktop (the 3.4 GB database became a
345 MB dump in about a minute). **Procedure:** the copy off the node, and a real restore.

The CronJob `postgres-backup` runs every night at 02:30: `pg_dumpall --globals-only` (the roles) and `pg_dump -Fc` of
every database into the PVC `postgres-backups`, as `/backups/<date-time>/`, keeping 7 days. That volume is on the
database node, next to the data: it protects against mistakes (a dropped table, a bad migration), **not** against
losing the node or its disk. Production adds one of:

* **Copies off the node:** a second CronJob (or a second step in this one) that mounts `postgres-backups` and copies
  each new directory to object storage, e.g. with `rclone copy` or `aws s3 sync`, into a versioned bucket with
  object lock, credentials from a Secret (or the node's instance role).
* **Volume snapshots** from storage that supports them (a CSI driver with snapshots, or the cloud's disks instead of
  `local-path`), scheduled by the storage or the cloud.

These dumps restore a database to the moment of the dump: up to a day of writes can be lost. Point-in-time recovery
(WAL archiving), a standby with automatic failover and scheduled base backups to object storage are what an operator
such as **CloudNativePG** adds; it is the next step for production ([ADR 0006](../adr/0006-postgresql.md)).

Run one now and see what it wrote:

```bash
kubectl -n infra create job --from=cronjob/postgres-backup postgres-backup-now
kubectl -n infra logs -f job/postgres-backup-now
```

Alert when no backup succeeded in the last 26 hours: the job's log is in Loki as
`{namespace="infra", app="postgres-backup"}`; there is no alert rule for it yet.

**Restore drill, monthly** (a backup is only real once it has been restored): restore the latest dump into a new
database (chapter 9, "Backups and restores"), run the data invariants against it
(`PG_DATABASE=<that database> tools/load-tests/verify-data.sh`) and drop it. Write down how long it took: that is
your recovery time.

## 3. The cache node

**Tested here:** [`redis.yaml`](../../deploy/infra/redis.yaml) on Docker Desktop (`deploy/scripts/infra.sh up redis`).
**Procedure:** the node.

```bash
deploy/scripts/add-node.sh admin@pm-cache.example.internal cache --server https://k3s.example.internal:6443
kubectl -n infra rollout restart deployment/redis     # Redis moves there on its next start
```

Redis has no volume, so the node can be added at any time. Its settings are the ConfigMap `redis-config`
(`maxmemory 256mb`, `volatile-lru`, no persistence, `FLUSHALL`/`FLUSHDB`/`DEBUG` disabled), its password the
random Secret `infra/redis-auth`. Redis is a pure cache here: losing it costs database reads, never data, so it needs
no backups. A replica with Sentinel (or a managed service) removes the restart gap if that matters for latency.

## 4. The logs

**Tested here:** the same Loki, Grafana and Alloy, dashboards and alert rules run on Docker Desktop
(`deploy/scripts/infra.sh up logs`, Grafana through `kubectl -n infra port-forward svc/grafana 3000:3000`). **Procedure:** the
production settings.

The observability is **logs only** ([chapter 8](08-observability.md)) and optional. Alloy reads the log of every pod
in every namespace through the Kubernetes API (PostgreSQL and Redis included) and receives the browser's errors from
the gateway's `/collect`; Loki keeps them 14 days; Grafana shows them, with the two dashboards and the 12 alert rules
provisioned from [`logs.yaml`](../../deploy/infra/logs.yaml) and [`dashboards/`](../../deploy/infra/dashboards). They run on any
node without a taint; Loki's volume stays on the node where it was first created.

For production, in `logs.yaml`: configure Loki with object storage for its chunks and the retention period the
company requires (`retention_period` in the ConfigMap `loki-config`: 14 days, e.g. 30 days or longer for audit),
replace Grafana's anonymous administrator (`GF_AUTH_ANONYMOUS_*`, `GF_AUTH_DISABLE_LOGIN_FORM`) with your identity
provider (`GF_AUTH_GENERIC_OAUTH_*`) before exposing it anywhere but a port-forward, and send the alert notifications
to the on-call channel (the contact points in the ConfigMap `grafana-alerting`).

**Grafana Cloud (or any other Loki) instead:** set `LOKI_URL` (and `LOKI_USERNAME`, `LOKI_PASSWORD`) in the deploy
project for the environment. Its `infra:<env>` job's `infra.sh up` then runs only Alloy, which pushes there; no Loki
or Grafana runs in the cluster. Import the dashboards from `deploy/infra/dashboards/` there. Without any log
collector, set the gateway's `COLLECTOR_UPSTREAM` to `""` in `deploy/environments/<env>/gateway.yaml` (what
`LOGS=off` does locally), or the browser's error reports get a 502.

## 5. The Kubernetes cluster (k3s)

**Tested here:** the manifests render and validate for staging and production
(`deploy/scripts/ci/validate.sh`, the deploy pipeline's `validate:manifests`), and the same files run on Docker Desktop;
`infra/` and the local environment also run on a single-node k3s v1.34.1+k3s1 (a fresh Ubuntu 24.04 VM, set up by
`deploy/scripts/setup-ubuntu.sh`: ServiceLB, `local-path` volumes, k3s' metrics-server, NetworkPolicies enforced).
**Procedure:** a k3s cluster of several servers, as below.

```bash
# First server (pin the version: the manifests are validated against Kubernetes 1.34)
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.34.1+k3s1" K3S_TOKEN="$K3S_TOKEN" \
  sh -s - server --cluster-init --tls-san k3s.example.internal \
  --secrets-encryption --write-kubeconfig-mode 600
# The other two servers
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.34.1+k3s1" K3S_TOKEN="$K3S_TOKEN" \
  sh -s - server --server https://k3s-1.example.internal:6443 --secrets-encryption
```

**Dedicated nodes** join with `deploy/scripts/add-node.sh <user@host> database|cache|apps` (sections 2 and 3; `apps` is an
ordinary node without a taint). It pins the same k3s version.

k3s brings what the manifests need: Traefik as the Ingress controller (`ingressClassName: traefik`), metrics-server
(the two HorizontalPodAutoscalers), a network policy controller, so the NetworkPolicies (only the gateway reaches the
API and the web app; only the gateway reaches Alloy's `/collect` receiver) are enforced, and the `local-path` storage
class for the PersistentVolumeClaims of PostgreSQL, its backups and Loki. `--secrets-encryption` encrypts Secrets at
rest in etcd. Schedule etcd snapshots off the nodes (`--etcd-s3` flags): they hold every object, including the only
copy of the generated passwords (`postgres-admin`, `redis-auth`, the services' `database-credentials`), but not the
volumes, which the database's backups cover (section 2).

**Pulling from the GitLab registry.** The projects' images are private (`registry.gitlab.com/zeiss/backend/api`,
`…/frontend/web`, `…/frontend/gateway`), so the cluster needs a read-only credential. Create **one group
deploy token named exactly `gitlab-deploy-token`** with the `read_registry` scope in the `zeiss` group (group →
Settings → Repository → Deploy tokens): it reads both projects' registries. GitLab passes it to every job of the
group's projects as `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD`; the deploy project's `versions.sh` lists the published
versions with it, and its `deploy.sh` stores it in the namespace as the `docker-registry` Secret `gitlab-registry`,
which every Deployment in staging and production references in `imagePullSecrets` (`environments/<env>/registry.yaml`
and `api.yaml`). The job's own `CI_JOB_TOKEN` would not do: it expires with the job, and a pod rescheduled later could
no longer pull. No node configuration is needed; the local environment pulls nothing (local images). The shared
infrastructure uses public images only.

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

**Procedure.** The public certificate stops at Traefik. From there everything (gateway, API, PostgreSQL, Redis,
Alloy, Loki) talks over the cluster's pod network, which never leaves the private network; between nodes flannel
carries it in VXLAN, unencrypted. The API's connection strings use plain connections to the in-cluster Services
(`GSS Encryption Mode=Disable`, no `SSL Mode`). Encrypting that traffic is optional, and there are two ways:

* **All of it, without touching the applications:** install the k3s servers with
  `--flannel-backend=wireguard-native` (decide before the cluster is built); the nodes then tunnel the pod network
  through WireGuard (UDP 51820 between the nodes instead of 8472).
* **Per server:** TLS in PostgreSQL (`ssl = on` with a certificate from a Secret, as the comment at the end of the
  `postgresql.conf` in `postgres.yaml` says) and in Redis (`tls-port`, `tls-cert-file`, `tls-key-file`, the comment in
  `redis.yaml`), with certificates from an internal CA (step-ca, Vault PKI, cert-manager with a CA issuer) for the
  Service names; then `SSL Mode=VerifyFull` in the API's connection string and `,ssl=true` on its Redis connection.

**Clients outside the cluster always use TLS:** a managed database or cache instead of the in-cluster ones (the
comments in `deploy/environments/{staging,production}/api.yaml` say where), with `SSL Mode=VerifyFull`.
**Open item:** verifying a certificate needs the CA in the API pods (a Secret or ConfigMap with `ca.crt`, mounted
read-only, and `SSL_CERT_FILE` or Npgsql's `Root Certificate` pointing at it): a small change to
`deploy/apps/backend/deployment.yaml`, not yet made.

## 7. GitLab

**Tested here:** the scripts the pipelines call run locally and in clean containers of the jobs' images, and each
`.gitlab-ci.yml` is validated against GitLab's CI schema; the pipelines run on gitlab.com (test, build and scan on the
shared runners). **Procedure:** the rest of the GitLab configuration; the staging and production jobs have not run
against real clusters.

### Group, projects and branch protection

1. Create a group (here `zeiss`) and three projects in it: `backend` (`backend/`), `frontend` (`frontend/`) and
   `deploy` (`deploy/`). Each folder is the root of its project, with its own `.gitlab-ci.yml`, `.gitignore` and
   README. `docs/`, `tools/` and the root README stay local, in no project.
2. In each project, **protected branch `main`:** allowed to merge: Maintainers; force push off. Allowed to push: no
   one in backend and frontend; in deploy, Maintainers, because its own `update:staging` job pushes the new versions
   straight to `main` with its job token, as the Maintainer who owns the schedule (step 7).
3. **Protected tags** in backend and frontend (Settings → Repository → Protected tags): `v*`, allowed to create:
   Maintainers. A version tag publishes a version that staging then runs by itself, so only maintainers release.
4. **Merge requests:** pipelines must succeed; all threads resolved; at least one approval (Premium: code owners for
   `**/Migrations` in backend, `environments/production/` and `infra/` in deploy). A production release is a merge
   request in deploy, so its approval rule is the release approval.
5. **Container Registry** on in backend (image `api`) and frontend (images `web`, `gateway`). Settings → Packages and
   registries: a **cleanup policy** that keeps every version: keep tags matching `^[0-9]+\.[0-9]+\.[0-9]+.*$`,
   remove other tags (commit builds) older than 90 days except the last 50, and untagged manifests after 14 days. A
   published version is never overwritten without any setting: the publish jobs refuse to point an existing `X.Y.Z`
   at another image. (Immutable container tags, Settings → Packages and registries → Container registry → Protected
   container tags → Immutable, are GitLab Ultimate: an optional extra that enforces the same in the registry.)
6. Settings → CI/CD → General pipelines (deploy): **Prevent outdated deployment jobs** on, so an older pipeline never
   applies over a newer one. A rollback is a version commit with its own pipeline, so it needs no job retries.
7. **Pushing with the job token.** In deploy, Settings → CI/CD → Job token permissions: turn on **"Allow Git push
   requests to the repository"** (GitLab 18.4+, every tier, off by default). `update:staging` and
   `propose:production` then clone deploy, commit the new versions and push with their own `CI_JOB_TOKEN`; nothing is
   stored and nothing expires. (Project access tokens, the usual alternative, are a Premium feature; on a GitLab
   before 18.4 set the optional variable `DEPLOY_PUSH_TOKEN` to any token with `write_repository`, e.g. a bot user's
   personal access token.) The job token has the rights of the user who started the job, so that user must be
   allowed to push to the protected `main` (step 2). A push made with the job token starts no pipeline, which is why
   `update:staging` deploys its own commit in the same job. Backend and frontend get no credential of deploy at all.
8. **The schedule.** In deploy, Build → Pipeline schedules → New schedule: description "update staging", interval
   `*/15 * * * *`, target branch `main`, active. Create it as a Maintainer: the schedule's owner is the user its job
   pushes as. It runs only `update:staging` (once `STAGING_ENABLED` is set).

### Runners

| Project | Jobs | Runner | Why |
| --- | --- | --- | --- |
| backend, frontend | test, build, scan, publish, release | gitlab.com shared Linux runners (no tags), or your own: **privileged** Docker executor, "Run untagged jobs", instance runners turned off for the project | `docker:dind` for Testcontainers and image builds; publish and release only need gitlab.com (the registry and the Releases API) |
| deploy | `validate:*`, `update:staging`, `propose:production`, `deploy:*`, `smoke:*`, `infra:*` | shared runners | the deploy and infra jobs apply through the agent (or a kubeconfig); the update jobs need only gitlab.com; the smoke jobs call the public URL |

No runner needs to be inside the private network: the API pods apply the migrations themselves, inside the cluster
([chapter 9](09-deployment-and-operations.md#the-api-applies-its-own-migrations-when-it-starts)), and
nothing is installed on the machines by a pipeline. A self-hosted runner is only needed for a kubeconfig pointing at a
cluster API that the shared runners cannot reach ("One machine of our own" below).

### The GitLab agent for Kubernetes

The jobs that touch a cluster use `kubectl` through the agent: a pod in each cluster that connects **out** to GitLab,
so the clusters' API servers are never exposed. One agent per cluster, used by the only project that needs it, deploy
(backend and frontend never reach a cluster):

1. In the deploy project, `zeiss/deploy` (or a separate "cluster management" project), add
   `.gitlab/agents/production/config.yaml` (and `staging/config.yaml` for the staging cluster):

   ```yaml
   ci_access:
     projects:
       - id: zeiss/deploy
         access_as:
           ci_job: {}    # act as the CI job's identity: grant it rights with RBAC (below)
   ```

2. Operate → Kubernetes clusters → Connect a cluster → name `production`; GitLab shows a `helm upgrade --install`
   command with a token. Run it against the cluster (from an operator's machine over the VPN).
3. RBAC for the jobs: with `access_as: ci_job`, the job's identity is the group `gitlab:ci_job` plus
   `gitlab:project_env:<project id>:<environment>`. Bind the deploy project to what its scripts apply:

   | Project | Applies |
   | --- | --- |
   | deploy | `deploy.sh <env> apps`: the namespace `product-management` and everything in it (the `api`, `web` and `gateway` Deployments, Services, HPAs, PDBs, NetworkPolicies, the Ingress, the Secrets `gitlab-registry`, `api-secrets` and `redis-credentials`; reads `database-credentials`); in `infra` it reads the Secret `redis-auth`. `deploy.sh <env> infra`: the namespace `infra` and everything in it (StatefulSets, Deployments, the CronJob, PVCs, Services, ConfigMaps, Secrets, the ServiceAccount, NetworkPolicy), the ClusterRole/ClusterRoleBinding `infra-alloy` (read-only access to pods and their logs), and metrics-server in `kube-system` where the cluster has none (k3s has it) |

   The simplest correct start is `cluster-admin` bound to `gitlab:project_env:<project id>:production` for the
   deploy project; tighten it to a Role per namespace (and the one ClusterRole for the shared infrastructure) afterwards. People who run
   `create-database.sh` need `pods/exec` on `infra/postgres-0` and Secrets in the service's namespace; `add-node.sh`
   needs to label and taint nodes.
4. The jobs select the cluster with `KUBE_CONTEXT=<agent configuration project path>:<agent name>`, e.g.
   `zeiss/deploy:production`.

Without the agent, a **kubeconfig** works too: a File-type variable `KUBECONFIG`, scoped to the environment, whose
current context is the cluster. The deploy scripts (`scripts/deploy.sh` in deploy) use
`KUBE_CONTEXT` when it is set (GitLab also sets a `KUBECONFIG` with the agent's contexts in every job of a project the
agent is shared with), else the kubeconfig's current context; they fail with a message naming `KUBE_CONTEXT` and
`KUBECONFIG` when neither is there. A kubeconfig means the
runner reaches the cluster's API (port 6443): a self-hosted runner next to the cluster, not the shared runners (see
"One machine of our own" below).

### Environments

Operate → Environments: `staging` and `production` are created by each project's first pipeline that reaches them. Then, in each
project, Settings → CI/CD → **Protected environments**: protect `production`, allowed to deploy: Maintainers (or a
"release managers" group), **required approvals: 1** (Premium). Protect `staging` as well (allowed to deploy:
Developers + Maintainers) so its protected variables are only exposed to jobs on `main`.

Jobs of the same environment must never overlap and must run oldest first, in the deploy project:

```bash
for project in "$DEPLOY_PROJECT_ID"; do
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

**No password of PostgreSQL or Redis is a variable** in any project: `infra.sh` and `create-database.sh` generate them
in the cluster, and the API reads them there (Secrets `database-credentials` and `redis-credentials`).

**Switches** (deploy project or group, scope **all**: the pipelines' `rules` decide with them which jobs exist, and
rules cannot see environment-scoped variables). Until they are set, the deploy pipeline stops after validate and is
green; nothing tries to reach a cluster that does not exist yet. Backend and frontend have no switch: they only
publish.

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `STAGING_ENABLED` | Variable | | | all | `true`: the deploy project's staging jobs appear (`update:staging` on the schedule, `deploy:staging`, `smoke:staging`, the manual `infra:staging`) |
| `PRODUCTION_ENABLED` | Variable | | | all | `true`: the production jobs appear (`propose:production` and `deploy:production`, both manual, `smoke:production`, `infra:production`) |

**Deploy token** (not a variable): `gitlab-deploy-token`, scope `read_registry`, once in the `zeiss` group (group →
Settings → Repository → Deploy tokens; a project-level deploy token would read only that project's images). GitLab
exposes it to every job as `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD`; `versions.sh` reads the registries with it, and
`deploy.sh` stops with a clear message without it
(section 5, "Pulling from the GitLab registry"). A server that runs published images outside the pipeline
(`deploy.sh local --version`, "One machine of our own" below) gets a separate group deploy token, e.g. `server-pull`
(`read_registry` only, with an expiry date), so it can be revoked on its own.

**Group level** (on GitLab Free, where environment-scoped group variables are not available, set them in each
project that needs them):

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `KUBE_CONTEXT` | Variable | ✔ | | staging, production | `zeiss/deploy:<agent>` (only deploy reaches a cluster) |
| `KUBECONFIG` | **File** | ✔ | | staging, production | instead of `KUBE_CONTEXT`: a kubeconfig whose current context is the cluster (self-hosted runner next to it) |

**deploy:**

| Variable | Type | Protected | Masked | Scope | Value |
| --- | --- | :---: | :---: | --- | --- |
| `API_SIGNING_KEY` | **File** | ✔ | | staging, production | `docker run --rm <api image> generate-signing-key` (a different key per environment) → Secret `api-secrets` |
| `API_CONSUMERS` | **File** | ✔ | | staging, production | `consumers.json` (below) → Secret `api-secrets` |
| `SMOKE_CLIENT_ID` | Variable | ✔ | | staging, production | optional: a viewer consumer, for the smoke check's signed-in read |
| `SMOKE_CLIENT_SECRET` | Variable | ✔ | ✔ (hidden) | staging, production | its secret |
| `LOKI_URL` | Variable | ✔ | | staging, production | optional: another Loki's push URL (e.g. Grafana Cloud); then no Loki or Grafana runs in the cluster (infra jobs) |
| `LOKI_USERNAME`, `LOKI_PASSWORD` | Variable | ✔ | ✔ (password) | staging, production | only with `LOKI_URL`, for a hosted Loki |
| `STAGING_URL`, `PRODUCTION_URL` | Variable | | | all | the public URLs the smoke jobs call (defaults in `.gitlab-ci.yml` are the example names) |
| `DEPLOY_PUSH_TOKEN` | Variable | ✔ | ✔ (hidden) | all | **optional, normally not set**: only on a GitLab before 18.4, where the job token cannot push (section 7, step 7); any token with `write_repository` on deploy, e.g. a bot user's personal access token. Otherwise `update:staging` and `propose:production` push with `CI_JOB_TOKEN` |

**backend, frontend:** no variable. Their pipelines use only predefined ones: `CI_REGISTRY_*` to push and tag images,
the job token for the Release. What matters there are the protected tags `v*`; the publish jobs themselves never
overwrite a published version.

The registry credentials (`CI_REGISTRY_*`) are predefined. Masking requires values of at least 8 characters from the
Base64 alphabet; that is why the consumer secrets below are hex. No connection string holds a password: the API's
connection strings are in `deploy/environments/<env>/api.yaml`, and the passwords reach the API separately, through
Secrets (`PGPASSWORD`, `Redis__Password`).

**The consumers file.** Only PBKDF2 hashes go in it; the secrets themselves go to the consumers (and, for staging, to
whoever runs the end-to-end tests and the requirements check by hand, and to `SMOKE_CLIENT_SECRET`). For each
consumer:

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

Staging needs `viewer`, `clerk` and `manager` (the frontend's Playwright suite, run by hand with
`E2E_<ROLE>_SECRET`, signs in as each, and `tools/check-requirements.py` as a manager). Production has only real
consumers (and, for the smoke check, optionally a viewer). Never reuse the local demo hashes generated by
`deploy/scripts/lib/local-secrets.sh`.

### What the pipelines do with them

| Project | Stages | Releases / deploys |
| --- | --- | --- |
| backend | test (`test:backend`: format, build, 390 tests on dind; SAST, secret detection) → build (`build:api`, `migrations:sql`) → scan (`scan:api`) | on a tag `vX.Y.Z`: `publish:api` (`api:X.Y.Z` on the tested image) → `release` (the GitLab Release with the digest and the changes) |
| frontend | test (`test:frontend`: lint, format, 187 tests, build; SAST, secret detection) → build (`build:web`, `build:gateway`) → scan (`scan:web`, `scan:gateway`) | on a tag `vX.Y.Z`: `publish:web`, `publish:gateway` → `release` |
| deploy | validate (`validate:manifests`: `scripts/ci/validate.sh` on every environment, `infra/` and `argocd/`; `validate:scripts`: `shellcheck`; `validate:alloy`: `alloy validate` and `alloy fmt` on the inline Alloy configuration) | `update:staging` (schedule: the newest versions, committed to `main`) → `deploy:staging` (`scripts/deploy.sh staging apps`: Secrets `gitlab-registry`, `api-secrets`, `redis-credentials`, apply, rollouts; the new API pods migrate the database as they start) → `smoke:staging` (`scripts/ci/smoke.sh $STAGING_URL`); `propose:production` (**manual**: the merge request) → `deploy:production` (**manual**, after it is merged) → `smoke:production`; `infra:staging` / `infra:production` (**manual**: `scripts/deploy.sh <env> infra`, with `LOKI_URL` when set) |

Merge request pipelines test, build and scan (deploy: validate); version tags also publish and release; pipelines on
the deploy project's `main` also deploy, once `STAGING_ENABLED` / `PRODUCTION_ENABLED` are `true`. No pipeline runs `create-database.sh`: a service's database is
created once per environment by an operator (section 8). The end-to-end tests and `tools/check-requirements.py` are
not pipeline jobs; run them by hand against staging. Details per job:
[chapter 9](09-deployment-and-operations.md#cicd-pipelines).

### One machine of our own

The short path to a running staging without cloud machines or the agent: k3s, the shared infrastructure and a GitLab
Runner on one Linux machine, the jobs reaching the cluster with a kubeconfig. Step 1 is **tested here** (a fresh
Ubuntu 24.04 VM, where the local environment then ran end to end); the rest is a **procedure**.

1. **k3s**, one server, on Ubuntu 22.04 or 24.04: clone the deploy repository and run
   `scripts/setup-ubuntu.sh`. It installs, pinned, Docker Engine 29.9.0, kubectl v1.34.1 and k3s v1.34.1+k3s1 as a
   single server (k3s' own kubeconfig stays readable by root only; yours is a copy, `~/.kube/config`, context
   `default`), waits for k3s' add-ons and prints the next commands; it is safe to run again. With only the deploy
   repository, `scripts/infra.sh up` and
   `REGISTRY_USER=… REGISTRY_PASSWORD=… scripts/deploy.sh local --version 1.0.0` give the whole system on
   http://&lt;server-ip&gt;:8080 from the published images (a group deploy token of its own with `read_registry`,
   e.g. `server-pull`; k3s pulls with it), the quickest check that the machine works; `./menu.sh` does the same from a
   menu. With backend and frontend cloned next to it, `scripts/deploy.sh local` builds the images there instead and
   copies them into k3s, no registry involved.
2. **Shared infrastructure**, from the deploy folder with the machine's kubeconfig (`~/.kube/config`):
   `scripts/deploy.sh staging infra` (or, once step 4 is done, the deploy project's `infra:staging`), then
   `scripts/lib/create-database.sh product-management product_management pm_app pm_migrator`. PostgreSQL, Redis and the
   logs run on the one node; `environments/staging/api.yaml` already names them.
3. **Runner:** Docker is already there (step 1). Install GitLab Runner from GitLab's repository
   (`curl -L "https://packages.gitlab.com/install/repositories/runner/gitlab-runner/script.deb.sh" | sudo bash`, then
   `sudo apt install -y gitlab-runner`), create a runner for the group (Settings → CI/CD → Runners → New group
   runner, "Run untagged jobs" on; it shows a `glrt-` token) and register it with the Docker executor:
   `sudo gitlab-runner register --non-interactive --url https://gitlab.com --token <glrt-token> --executor docker --docker-image alpine:3.22 --docker-privileged`
   (privileged for docker:dind). Turn off instance runners in the three projects, so every job, deploys included,
   runs here and can reach the cluster's API.
4. **Cluster access:** in deploy a File variable `KUBECONFIG`, scope `staging`, with the content
   of `~/.kube/config` (the same as `/etc/rancher/k3s/k3s.yaml`) where `server: https://127.0.0.1:6443` is replaced by the machine's LAN address
   (inside a job's container, 127.0.0.1 is the container). No agent, and the cluster's API is not exposed beyond
   the machine's network.
5. **The rest of section 7:** the group deploy token, the job token push setting, the schedule, the staging variables of
   the deploy project (the signing key, the consumers), the staging Ingress host in
   `deploy/environments/staging/ingress.yaml` and `STAGING_URL` (a name that resolves to the machine; cert-manager as
   in section 5 for a certificate the smoke job accepts), then `STAGING_ENABLED=true`. Tag `v1.0.0` in backend and in
   frontend; the next `update:staging` commits those versions, and the deploy project's pipeline applies staging (the
   API migrates its database as it starts) and smoke-checks it.

More machines later join with `deploy/scripts/add-node.sh` (a `database` node before PostgreSQL holds data worth keeping:
section 2).

The k6 smoke test is not in the GitLab pipelines: its scripts sign in with the local demo secrets, so it would need
those consumers in staging. Run it by hand after a local deployment (`tools/load-tests/run.sh smoke`); like
`tools/check-requirements.py`, it is a local tool, in no GitLab project.

## 8. The first deployment, in order

1. **Network and machines:** private network, DNS names, the firewall rules (section 1).
2. **Cluster:** the k3s servers (section 5), then the dedicated nodes with `deploy/scripts/add-node.sh`: the
   `database` node **before** anything starts PostgreSQL (section 2), the `cache` and `apps` nodes as needed;
   cert-manager and the `letsencrypt` ClusterIssuer; the public DNS records.
3. **GitLab:** the group and the three projects, protections, registry rules, the group deploy token
   (`gitlab-deploy-token`), protected tags `v*` in backend and frontend, "Allow Git push requests to the repository"
   for the job token in deploy and the `update:staging` schedule (owned by a Maintainer), the agent and its RBAC, protected environments, resource groups, every
   variable of section 7 (the switches `STAGING_ENABLED` / `PRODUCTION_ENABLED` last, once the cluster is up).
4. **Shared infrastructure:** in the deploy project run `infra:staging` (`scripts/deploy.sh staging infra`, which runs
   `infra.sh up`). Check it: `kubectl -n infra get pods,pvc -o wide` (PostgreSQL on the database node, all volumes
   bound).
5. **The service's database**, once per environment, from an operator's machine with the environment's context:
   `deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator`.
6. **The deploy repository:** replace the `example.*` names in `deploy/environments/{staging,production}/ingress.yaml`
   (Ingress hosts) and set `STAGING_URL` / `PRODUCTION_URL`; merge it. Its pipeline validates; `deploy:staging` stops
   with "names no version yet for some images (newTag: unreleased)": `scripts/deploy.sh` refuses an environment until
   every image has a version. That is expected before the first releases.
7. **Release the backend.** Merge into `main` (read `migrations.sql` in the merge request), then tag `v1.0.0` on `main`
   (backend README, "How to release a version"). The tag's pipeline publishes `api:1.0.0` and the Release. The next
   `update:staging` commits `staging: api 1.0.0`; the deploy pipeline of that commit still stops at `deploy:staging`
   for the same reason (web and gateway have no version yet).
8. **Release the frontend** the same way (`v1.0.0`): `update:staging` commits `staging: web 1.0.0, gateway 1.0.0`.
   Now every image of staging has a version, and the deploy project's pipeline on that commit runs `deploy:staging`
   (apply; the new API pods migrate the database first) and `smoke:staging`. Watch the first certificate being issued:
   `kubectl -n product-management get certificate,ingress`.
9. **Production:** set `PRODUCTION_ENABLED=true`; run the deploy project's `infra:production` and `create-database.sh`
   against the production cluster (steps 4 and 5). Start `propose:production` (or run
   `deploy/scripts/set-version.sh production all 1.0.0` and open a merge request yourself). Merge it (it is reviewed:
   it is the release decision), then start `deploy:production` in the deploy project (approve it if approvals are
   required): it applies, and the new API pods migrate production's database as they start; `smoke:production`
   follows.
10. **Check by hand:** sign in on `https://products.example.com`, look at the API and gateway logs in Grafana
    (`kubectl -n infra port-forward svc/grafana 3000:3000`), run
    `BASE_URL=https://products.example.com CHECK_CLIENT_ID=... CHECK_CLIENT_SECRET=... python3 tools/check-requirements.py`
    once with a production manager consumer (it creates and deletes its own products), and start one backup
    (section 2).

### Argo CD (prepared)

The deploy repository already holds what Argo CD needs (**prepared and validated, not installed**): `argocd/` with one
`Application` per environment (`environments/<env>` of `zeiss/deploy` on `main`; automated sync with prune and
selfHeal for staging, manual sync for production), Argo CD Image Updater's annotations on the staging Application
(semver strategy, only `X.Y.Z`, git write-back to `main`: it replaces `update:staging`) with the `ImageUpdater` object
that Image Updater 1.x reads. No migration hook is needed: the API pods Argo CD rolls out migrate the database
themselves. `scripts/ci/validate.sh` checks them against the CRDs of the pinned
versions (Argo CD v3.5.4, Image Updater v1.3.1) on every change.

The switch (**procedure**; exact commands in the deploy README, "Moving to Argo CD"): install Argo CD and Image
Updater with their pinned manifests; register `zeiss/deploy` over SSH (`git@gitlab.com:zeiss/deploy.git`) with a read-only deploy key; create the
Secrets `gitlab-registry` (registry) and `deploy-write-key` (write-back: an SSH deploy key with write access, allowed
to push to `main`; key `sshPrivateKey`) in `argocd`;
in one merge request switch off `deploy:*`, `smoke:*` and
`update:staging` (deactivate the schedule); then `kubectl apply -k argocd/applications`. Nothing changes in backend
and frontend: they still only publish versions. Production still changes by merge request, and the release becomes
the Sync button. The Secrets `deploy.sh` creates from CI/CD variables (`gitlab-registry`, `api-secrets`) move to a
secrets operator (e.g. External Secrets) or stay a one-off step per environment; `infra/` stays a deliberate, manual
apply.

## 9. Day 2

**Procedure** for everything in this section, except where marked.

### Roll back a release

In the deploy project, set the previous version: `deploy/scripts/set-version.sh production api 1.3.2` (it checks the
version exists), through a merge request (or `git revert` the version commit). For staging, deactivate the
`update:staging` schedule first, or it moves staging forward again within 15 minutes; usually a fixed version
(`v1.4.1`) is the better way out. The change sets the older `newTag` in `environments/<env>/kustomization.yaml`; the
pipeline applies it (`deploy:production` is started by hand, as always) and smoke-checks it, and
`git log environments/<env>` shows the rollback like any release. The database is not rolled back: every migration is backward compatible with the release
before it (expand/contract), and a bad migration is fixed by a new one. In an emergency,
`kubectl -n product-management rollout undo deployment/api` (and `gateway`, `web`) is faster; the next deploy job
applies `main` again, so follow it with the revert (**tested here** locally).

### Rotate secrets

The API's own secrets are rotated by changing a variable of the deploy project and re-running its last
`deploy:<env>` job: `deploy.sh` updates `api-secrets`, sees that it changed, and restarts the API so the pods read it.
The database and Redis passwords live only in the cluster and are rotated there, from an operator's machine with the
environment's context.

* **Signing key:** generate a new key, replace the `API_SIGNING_KEY` file variable, re-run the deploy project's
  `deploy:<env>` job.
  Tokens signed with the old key are refused: every consumer signs in again (tokens live one hour). Do it at a quiet
  time.
* **Consumers:** edit `API_CONSUMERS`, re-run the deploy project's `deploy:<env>` job (the API reads the file at
  startup, and the job restarts it).
* **Database passwords** (`pm_app` and `pm_migrator` together):

  ```bash
  deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator --rotate
  kubectl -n product-management rollout restart deployment/api
  ```

  PostgreSQL checks passwords only when a connection opens, so open connections survive; new connections of pods
  not yet restarted fail until the rolling restart replaces them, so run the two together. The restarted pods also
  read the new migrator password (for their migration step at startup).
* **Redis:** a new random password, then Redis, then the services that copy it:

  ```bash
  kubectl -n infra delete secret redis-auth
  deploy/scripts/infra.sh up redis     # or the deploy project's infra:<env> job: a new redis-auth, Redis restarted
  ```

  Then re-run the deploy project's `deploy:<env>` job: `deploy.sh` copies the new password into `redis-credentials`
  and, because it changed, restarts the API, which reads it at startup (a local `deploy.sh local` does the same). Until then the API cannot reach Redis: the cache and the shared rate limits
  fall back to their degraded mode and idempotent writes answer 503, so do it at a quiet time. Every other service
  that uses the shared Redis needs the same redeploy.
* **Hosted Loki token:** update `LOKI_PASSWORD` and re-run the deploy project's `infra:<env>` job; `infra.sh` updates the
  Secret `loki-external` and restarts Alloy.

### Scale

The HorizontalPodAutoscalers own the replica counts (API 2–8, gateway 2–6). To change the range, edit
`deploy/apps/backend/hpa.yaml` (or the gateway's HPA in `deploy/apps/frontend/gateway.yaml`, or patch either in
`deploy/environments/<env>`) and merge it: keep `maxReplicas × 11` connections within what PostgreSQL can serve
([chapter 6](06-scaling-and-capacity.md)). More room for the pods: `deploy/scripts/add-node.sh <user@host> apps`. A bigger
database is a bigger database node, with the limits and memory settings of section 2 raised (or a read replica for
lists and searches, which needs an operator and a code change).

### Upgrade

* **k3s:** one node at a time: `kubectl drain <node> --ignore-daemonsets --delete-emptydir-data`, re-run the installer
  with the next patch or minor `INSTALL_K3S_VERSION`, `kubectl uncordon <node>`. The PodDisruptionBudgets keep an API,
  gateway and web pod up throughout. PostgreSQL and Loki cannot move off their node (their volumes are there), so
  draining the database node stops PostgreSQL until the node is back: do that node in a maintenance window. Never skip
  a minor version; bump the `KUBE_TOOLS_IMAGE` kubectl in the three pipelines and `KUBERNETES_VERSION` in the
  deploy pipeline (and the default in `scripts/ci/validate.sh`) with it, and `K3S_VERSION` in `add-node.sh`.
* **PostgreSQL minor:** set a newer `postgres:18.x-alpine` tag in `postgres.yaml` (the floating `18-alpine` is not
  pulled again by a node that already has it) and `deploy/scripts/infra.sh up postgres` (the `infra:<env>` job): the StatefulSet restarts the pod
  (seconds of connection errors; the API retries reads, and the readiness probe tolerates 30 s). **Major:** a dump and
  restore into the new version, rehearsed on a copy first, then for real in a maintenance window (or an operator's
  in-place upgrade).
* **Images and tools:** the pinned versions are at the top of each project's `.gitlab-ci.yml` and in the deploy
  project's `infra/` manifests (`postgres.yaml`, `redis.yaml`, `logs.yaml`); base images in the Dockerfiles are rebuilt on every pipeline
  (`--pull`), and Trivy fails the pipeline when a critical fix is available.

### Restore the database

For data loss or corruption, restore the last good nightly dump of the service's database: the commands are in
[chapter 9](09-deployment-and-operations.md), "Backups and restores" (a pod with the backups' volume and
`pg_restore --clean --if-exists`, then `create-database.sh` and a deploy). Around it:

1. Stop writes: remove the autoscaler (its minimum cannot be 0 with CPU metrics) and scale the API to zero:
   `kubectl -n product-management delete hpa api && kubectl -n product-management scale deployment/api --replicas=0`.
   The gateway keeps serving the web app and answers API calls with 502 meanwhile.
2. Restore the dump, then check it: `tools/load-tests/verify-data.sh` (it uses `kubectl exec` into PostgreSQL).
3. Empty the cache, which must not outlive the data (`kubectl -n infra rollout restart deployment/redis`), and re-run
   `create-database.sh` (it hands the restored objects to `pm_migrator`). Re-run the deploy project's last
   `deploy:<env>` job: it applies again, which recreates the HPA, and the API pods, as they start, re-apply any missing
   migration (and the app user's rights). Then `kubectl -n product-management scale deployment/api --replicas=2`.

Writes after the dump are lost; point-in-time recovery needs WAL archiving (section 2).

**The database node is lost, or PostgreSQL moves to another node.** The volume `data-postgres-0` belongs to its node,
so PostgreSQL starts fresh on the new one and the data comes back from a dump (from object storage when the old node
is gone):

```bash
deploy/scripts/add-node.sh admin@pm-db-2.example.internal database --server https://k3s.example.internal:6443
kubectl delete node <old node>                                  # only when the old node is gone for good
kubectl -n infra scale statefulset/postgres --replicas=0
kubectl -n infra delete pvc data-postgres-0 postgres-backups    # the volumes on the old node
deploy/scripts/infra.sh up postgres                             # new, empty volumes on the database node
deploy/scripts/lib/create-database.sh product-management product_management pm_app pm_migrator   # same passwords (from the Secret)
```

Then restore the dump: start the restore pod of chapter 9, copy the file into it
(`kubectl cp product_management.dump infra/postgres-restore:/tmp/`) and run `pg_restore` on `/tmp/…` as there, with
writes stopped as above. Moving a healthy database works the same way, with a fresh dump taken first
(`kubectl -n infra create job --from=cronjob/postgres-backup …`) and copied out of the volume
(`kubectl cp infra/postgres-restore:/backups/<date-time> ./<date-time>`) before the volumes are deleted. The
services' connection strings do not change: the Service name stays.

### Read the logs

Grafana (`kubectl -n infra port-forward svc/grafana 3000:3000`) → Explore → Loki:
`{namespace="product-management", app="api"}` for the API, `{app="gateway"}` for the access log; the shared
infrastructure under `{namespace="infra"}`, e.g. `{namespace="infra", app="postgres"}` or
`{namespace="infra", app="postgres-backup"}` ([chapter 8](08-observability.md)). Without Grafana:
`kubectl -n product-management logs deployment/api`, `kubectl -n infra logs statefulset/postgres`.

## 10. Production checklist

| | Item |
| :---: | --- |
| ☐ | Private network; firewall rules exactly as in section 1; no public address but the Ingress; no PostgreSQL, Redis, Loki or Grafana port on any machine |
| ☐ | Cluster: 3 k3s servers, Secrets encrypted at rest, etcd snapshots off the nodes, metrics-server running (`kubectl top nodes`) |
| ☐ | Nodes: the `database` node added before PostgreSQL started, `postgres-0` running on it (`kubectl -n infra get pod -o wide`); `cache` / `apps` nodes as needed |
| ☐ | PostgreSQL: limits and memory settings sized to the node, disk sized for data and dumps, `create-database.sh` run for each service in each environment |
| ☐ | Backups: the nightly dump succeeding, copied off the node (object storage) or volume snapshots, a restore drill done and timed, alert on missing backups; CloudNativePG (or another operator) planned for failover and point-in-time recovery |
| ☐ | Redis: `maxmemory` sized, dangerous commands disabled (in `redis.yaml`) |
| ☐ | Logs: Loki retention and storage, Grafana behind SSO (or only through port-forwards), alerts routed to on-call; or `LOKI_URL` to a hosted Loki |
| ☐ | TLS: certificate issued for the public names, HTTP redirects to HTTPS, HSTS; inside the cluster a decision (WireGuard pod network, per-server TLS, or plain on the private network); `VerifyFull` with the CA mounted for any server outside the cluster |
| ☐ | GitLab: three projects, `main` protected (deploy: pushable by Maintainers, for the job token), protected tags `v*` in backend and frontend, MR approvals (production releases in deploy), a cleanup policy that keeps every version, group deploy token `gitlab-deploy-token` (`read_registry`, on the zeiss group), "Allow Git push requests to the repository" (job token) in deploy, the `update:staging` schedule owned by a Maintainer |
| ☐ | Agent: one per cluster, used by the deploy project only, RBAC limited to what its scripts apply |
| ☐ | Environments: `production` protected with required approval; resource groups `oldest_first` |
| ☐ | Variables: all of section 7, protected and masked as listed, `STAGING_ENABLED` / `PRODUCTION_ENABLED` set, a new signing key per environment, no local demo values |
| ☐ | `example.*` names replaced in `deploy/environments/{staging,production}/ingress.yaml` and in `STAGING_URL` / `PRODUCTION_URL` |
| ☐ | A full release green through staging: a version tag in backend and frontend published, `update:staging` committed it, deploy applied it (the API migrated) and smoke-checked; one production release (merge request) and one rollback (`set-version.sh` with the older version) rehearsed |
| ☐ | The HPAs' limits re-measured on the real nodes ([chapter 6](06-scaling-and-capacity.md)) |
| ☐ | Runbooks for this chapter's day-2 procedures linked from the alerts |

## Where to look

* Pipelines: [`deploy/.gitlab-ci.yml`](../../deploy/.gitlab-ci.yml), [`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml),
  [`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml)
* Pipeline scripts: [`deploy/scripts/deploy.sh`](../../deploy/scripts/deploy.sh),
  [`deploy/scripts/ci/validate.sh`](../../deploy/scripts/ci/validate.sh), [`deploy/scripts/ci/smoke.sh`](../../deploy/scripts/ci/smoke.sh),
  [`versions.sh`](../../deploy/scripts/versions.sh),
  [`set-version.sh`](../../deploy/scripts/set-version.sh), [`update-versions.sh`](../../deploy/scripts/ci/update-versions.sh),
  [`backend/ci/release-notes.sh`](../../backend/ci/release-notes.sh) (the same in frontend); Argo CD:
  [`deploy/argocd/`](../../deploy/argocd)
* Environments: [`deploy/environments/staging`](../../deploy/environments/staging),
  [`deploy/environments/production`](../../deploy/environments/production); the apps
  [`deploy/apps/backend`](../../deploy/apps/backend), [`deploy/apps/frontend`](../../deploy/apps/frontend)
* Shared infrastructure: [`deploy/infra/`](../../deploy/infra) ([`postgres.yaml`](../../deploy/infra/postgres.yaml),
  [`redis.yaml`](../../deploy/infra/redis.yaml), [`logs.yaml`](../../deploy/infra/logs.yaml),
  [`dashboards/`](../../deploy/infra/dashboards)), [`infra.sh`](../../deploy/scripts/infra.sh),
  [`create-database.sh`](../../deploy/scripts/lib/create-database.sh), [`add-node.sh`](../../deploy/scripts/add-node.sh), and
  the deploy repository's [README](../../deploy/README.md)
* Decisions: [ADR 0021 deployment layout](../adr/0021-deployment-layout.md), [ADR 0006 PostgreSQL](../adr/0006-postgresql.md)

## How to verify

What can be checked without real machines (**tested here**):

```bash
# Every environment, infra/ and argocd/ render and are valid; the settings each must set; no Secret in staging/production;
# staging and production pull with the Secret gitlab-registry (what the deploy pipeline's validate:manifests runs)
docker run --rm -v "$PWD/deploy:/work:ro" -w /work alpine/k8s:1.34.11 scripts/ci/validate.sh

# What staging and production would receive
kubectl kustomize deploy/environments/staging | less
kubectl kustomize deploy/environments/production | less

# The scripts and the Alloy configuration (validate:scripts, validate:alloy)
docker run --rm -v "$PWD/deploy:/mnt:ro" -w /mnt koalaman/shellcheck-alpine:v0.10.0 shellcheck menu.sh scripts/*.sh scripts/ci/*.sh scripts/lib/*.sh
alloy_dir="$(mktemp -d)"
docker run --rm -v "$PWD/deploy:/work:ro" -w /work alpine/k8s:1.34.11 \
  yq 'select(.kind == "ConfigMap" and .metadata.name == "alloy-config") | .data["config.alloy"]' infra/logs.yaml \
  > "$alloy_dir/config.alloy"
docker run --rm -v "$alloy_dir:/work:ro" --entrypoint alloy grafana/alloy:v1.10.2 validate /work/config.alloy

# What update:staging would commit, without pushing (a clone of the deploy repository; a registry token)
DRY_RUN=1 DEPLOY_REPO_URL=<path to a clone of zeiss/deploy> REGISTRY_USER=<user> REGISTRY_PASSWORD=<token> \
  deploy/scripts/ci/update-versions.sh staging

# The release notes of a version tag, from the repository's history (in a clone of backend with its tags)
ci/release-notes.sh v1.4.0 registry.gitlab.com/zeiss/backend/api:1.4.0

# The smoke check the deploy pipeline runs, against the local deployment
deploy/scripts/ci/smoke.sh http://localhost:8080

# What add-node.sh would do on a real machine (prints the steps, changes nothing)
deploy/scripts/add-node.sh admin@pm-db.example.internal database --server https://k3s.example.internal:6443 --dry-run

# The SQL the migrations run (the pipeline's review artifact)
cd backend && dotnet tool restore && dotnet restore ProductManagement.sln && dotnet ef migrations script --idempotent \
  --project ProductManagement.Infrastructure --startup-project ProductManagement.Api --output ../migrations.sql

# Each pipeline's structure (resolves GitLab's templates), from inside each repository
cd backend && npx gitlab-ci-local@4 --list
```
