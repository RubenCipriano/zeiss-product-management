# Shared infrastructure

PostgreSQL, Redis and the logs (Loki, Grafana, Alloy) for every service on the cluster, all in Kubernetes, in one
namespace: `infra`. This folder is self-contained, its own repository ([gitlab.com/zeiss/deploy](https://gitlab.com/zeiss/deploy)).
The same files run on Docker Desktop's Kubernetes on a laptop and on a k3s cluster in production.

| File | What it is |
| --- | --- |
| [`postgres.yaml`](postgres.yaml) | the namespace `infra`; PostgreSQL 18 (StatefulSet, one replica, 20 GiB volume), its settings inline (`postgresql.conf`), Service `postgres:5432`; a nightly `pg_dump` of every database (CronJob `postgres-backup`, 7 days, on its own volume) |
| [`redis.yaml`](redis.yaml) | Redis 8 (Deployment, one replica, memory only), its settings inline (`redis.conf`), Service `redis:6379` |
| [`logs.yaml`](logs.yaml) | Loki (StatefulSet, 10 GiB volume, 14 days), Grafana (data source, dashboard provider, the 12 alert rules, contact points and policies inline), Alloy (collects the logs of every pod in every namespace, and the browser's error reports behind Service `alloy-faro:12347`) |
| [`dashboards/`](dashboards) | the two Grafana dashboards, as JSON files; `infra.sh` loads them into the ConfigMap `grafana-dashboards` |
| [`infra.sh`](infra.sh) | starts, stops and shows the components |
| [`create-database.sh`](create-database.sh) | gives a service its database, users and a Secret with their passwords |
| [`add-node.sh`](add-node.sh) | joins a machine to a k3s cluster as a node for the database, the cache or the services |
| [`.gitlab-ci.yml`](.gitlab-ci.yml) | validates every change; deploys with `infra.sh up` (manual) |

Why everything is in the cluster: [ADR 0021](../docs/adr/0021-deployment-layout.md). What the logs contain and how to
read them: [chapter 8](../docs/guide/08-observability.md). Operations: [chapter 9](../docs/guide/09-deployment-and-operations.md).
Production: [chapter 12](../docs/guide/12-from-zero.md).

## Prerequisites

`kubectl` with a context: Docker Desktop's Kubernetes (`docker-desktop`) locally, or a k3s cluster. Nothing else:
`psql` and `redis-cli` run inside the pods (`kubectl exec`).

## Usage

```bash
./infra.sh up                          # everything: postgres, redis, logs
./infra.sh up postgres redis           # only these two (no logs)
./infra.sh down logs                   # remove a component; its volumes and passwords are kept
./infra.sh down postgres --delete-data # ... and delete its volumes and password too
./infra.sh status                      # pods, services, volumes, and how to reach them
```

`infra.sh up|down|status [postgres] [redis] [logs] [--delete-data]`; no component means all three.

* **`up`** creates, once, what must never be committed: the namespace and the random passwords (Secrets
  `postgres-admin`, the superuser `postgres`; `redis-auth`, Redis). It installs metrics-server (pinned, v0.8.0) when
  the cluster has none: the services' autoscalers need it; k3s ships it, Docker Desktop does not. Then it applies the
  component's file and waits until it is ready. A changed `redis.conf`, Loki, Grafana or Alloy configuration restarts
  that component; a changed `postgresql.conf` prints how to reload (or, for memory settings, restart) PostgreSQL.
* **`down`** deletes a component's workloads, Services and configuration. Its PersistentVolumeClaims
  (`data-postgres-0`, `postgres-backups`, `data-loki-0`) and password Secrets stay, and `down` lists them: `up` brings the
  component back with its data. Only `--delete-data` deletes them.
* **`status`** shows the pods, Services and volumes, and the commands to reach them from this machine.

Nothing is published on the host. From a laptop:

```bash
kubectl -n infra port-forward svc/grafana 3000:3000       # Grafana → http://localhost:3000 (folder "Product Management")
kubectl -n infra port-forward svc/loki 3100:3100          # Loki → http://localhost:3100
kubectl -n infra exec -it statefulset/postgres -- psql -U postgres -d product_management
kubectl -n infra exec -it deployment/redis -- redis-cli   # the pod has the password (REDISCLI_AUTH)
```

## A service's database

One PostgreSQL for the platform, one database per service:

```bash
./create-database.sh <service-namespace> <database> [app-user] [migrator-user] [--rotate]
./create-database.sh product-management product_management pm_app pm_migrator
```

It creates, idempotently:

* the database (nobody may connect to it but its two users);
* the **migrator** user (default `<database>_migrator`): owns the schema, runs the service's migrations, may create
  trusted extensions;
* the **app** user (default `<database>_app`): what the service runs as. It may only use the schema; its statements end
  after 30 s and an idle open transaction after 60 s. Which tables it may read and write is granted by the service's own
  migrations, which create those tables (product-management: the migration `GrantAppPermissions`);
* `pg_stat_statements` in the database;
* the Secret `database-credentials` in the service's namespace: `host`, `port`, `database`, `app-user`, `app-password`,
  `migrator-user`, `migrator-password`.

The passwords are random. Running it again keeps them (and hands tables owned by someone else, e.g. after a restore as
the administrator, over to the migrator); `--rotate` makes new ones, after which the service's pods are restarted to
read them.

The service then points its connection string at `postgres.infra.svc.cluster.local:5432` and reads the password from
the Secret (`PGPASSWORD` from `app-password` for Npgsql and libpq).

## A service's cache and logs

* **Redis:** `redis.infra.svc.cluster.local:6379`, one password for every service (Secret `redis-auth`). A pod can only
  read Secrets of its own namespace, so the service copies it into its namespace when it deploys (the backend's
  `deploy/deploy.sh` makes `redis-credentials`). Services separate their keys by prefix.
* **Logs:** nothing to do. Alloy collects the stdout of every pod in every namespace; in Loki they are
  `{namespace="<namespace>", app="<app.kubernetes.io/name label>"}`. The shared infrastructure's own logs are
  `{namespace="infra", app="postgres"}` (or `redis`, `loki`, `grafana`, `postgres-backup`).
* **Browser errors** (optional): the gateway forwards them to `alloy-faro.infra.svc.cluster.local:12347`. A
  NetworkPolicy admits only the gateway pods of `product-management`; another service's gateway is added there.

## Another Loki

```bash
LOKI_URL=https://<host>/loki/api/v1/push LOKI_USERNAME=<user> LOKI_PASSWORD=<token> ./infra.sh up logs
```

Then Loki and Grafana are not deployed (Loki's volume is kept), only Alloy is applied, and it pushes to `LOKI_URL` with
the credentials of the Secret `loki-external`. `./infra.sh up logs` without `LOKI_URL` switches back. The dashboards in
[`dashboards/`](dashboards) and the alert rules in [`logs.yaml`](logs.yaml) can be imported into that Grafana.

Without logs at all (`./infra.sh up postgres redis`) the services work the same: `kubectl logs` still shows every pod,
and the frontend is deployed with `LOGS=off` so the gateway answers the browser's error reports with 204.

## Backups

The CronJob `postgres-backup` dumps, every night at 02:30, the roles and each database into
`/backups/<date-time>/` on the volume `postgres-backups`, and keeps 7 days. It protects against mistakes (a dropped
table, a bad migration), not against losing the node: production ships the dumps off the node or uses volume snapshots
([chapter 12](../docs/guide/12-from-zero.md)).

```bash
kubectl -n infra create job --from=cronjob/postgres-backup postgres-backup-now   # one now
```

## Nodes

On Docker Desktop (and on a single k3s server) everything shares one node. A k3s cluster can give the database and the
cache machines of their own:

```bash
./add-node.sh admin@db-1.example.internal database --server https://k3s-1.example.internal:6443
./add-node.sh admin@cache-1.example.internal cache
./add-node.sh admin@apps-1.example.internal apps
./add-node.sh admin@db-1.example.internal database --dry-run      # print the steps only
```

Over ssh it installs the k3s agent (pinned to the cluster's version, v1.34.1+k3s1) with the server's join token, labels
the node `infra/role=<role>` and taints `database` and `cache` nodes `infra/role=<role>:NoSchedule`. PostgreSQL prefers
a `database` node and Redis a `cache` node, and both tolerate the taint, so nothing else runs there; without such nodes
the preference is simply not met. A volume on a node's disk stays on that node: add the database node before the first
`./infra.sh up postgres`, or move an existing database with a dump and restore. It cannot be tried on Docker Desktop,
whose Kubernetes is one fixed node.

## CI/CD (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml):

* **validate** (every change): `kubeconform` on the three manifests and the dashboards' ConfigMap, `jq` on the
  dashboards, `shellcheck` on the scripts, `alloy validate` and `alloy fmt` on the Alloy configuration taken out of
  `logs.yaml`.
* **deploy** (manual, on the default branch, per environment once `STAGING_ENABLED` / `PRODUCTION_ENABLED` is `true`):
  selects the cluster and runs `./infra.sh up`.

CI/CD variables, scoped to the `staging` and `production` environments:

| Variable | What |
| --- | --- |
| `KUBE_CONTEXT` | `<agent configuration project path>:<agent name>` (the GitLab agent for Kubernetes) |
| `KUBECONFIG` | File, instead of `KUBE_CONTEXT`: a kubeconfig whose current context is the cluster |
| `LOKI_URL`, `LOKI_USERNAME`, `LOKI_PASSWORD` | optional: another Loki (the last two masked) |

No password of PostgreSQL or Redis is a variable: `infra.sh` creates them in the cluster. A new service's database is a
deliberate, one-time step per environment: `./create-database.sh` against that cluster.
