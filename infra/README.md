# Product Management — Infra

The servers the Product Management system needs outside its Kubernetes cluster, and the cluster's log collector.
This folder is self-contained, meant to be its own repository. Locally every server is a Docker Compose container on
this machine; on real machines the same configuration files run on each server, or a managed service (Amazon RDS,
ElastiCache, Grafana Cloud) replaces it.

| Component | What | Files |
| --- | --- | --- |
| `database` | PostgreSQL 18 on port 5432, with the least-privilege users `pm_migrator` and `pm_app` | [`database.yaml`](database.yaml), [`postgres/`](postgres) |
| `redis` | Redis 8 on port 6379, password protected, memory only | [`redis.yaml`](redis.yaml), [`redis/`](redis) |
| `logs` (optional) | Loki on port 3100 (14 days), Grafana on http://localhost:3000 (dashboards, 12 alert rules), the server agent that ships the servers' logs, and the cluster's log collector | [`logs.yaml`](logs.yaml), [`logs/`](logs) |

Why the servers are outside the cluster: [ADR 0021](../docs/adr/0021-deployment-layout.md). What the logs contain
and how to read them: [chapter 8](../docs/guide/08-observability.md). Deployment and operations:
[chapter 9](../docs/guide/09-deployment-and-operations.md).

## Prerequisites

* Docker with the Compose plugin (Docker Desktop locally).
* `kubectl` with a context (e.g. `docker-desktop`) for the cluster's log collector; without a context that step is
  skipped.

## Usage

```bash
./infra.sh up                     # everything: database, redis, logs
./infra.sh up database redis      # only these two (no Loki, no Grafana)
./infra.sh up logs                # Loki, Grafana, the server agent and the cluster's log collector
./infra.sh down logs              # stop one component (and remove the cluster's collector); data volumes are kept
./infra.sh down                   # stop everything
./infra.sh status                 # the server containers and the cluster's collector
```

`infra.sh up|down|status [database] [redis] [logs]`; no component means all three. Each component is its own Compose
project (`pm-database`, `pm-redis`, `pm-logs`), so starting or stopping one never touches the others.

* **`up database`** creates the volume `pm-postgres-data`, starts `pm-postgres` (4 CPUs, 3 GB, 1 GB shared memory,
  [`postgres/postgresql.conf`](postgres/postgresql.conf)), and waits for the one-shot `roles` service
  (`pm-postgres-roles`), which on every `up` creates the `pg_stat_statements` extension and applies
  [`postgres/roles.sql`](postgres/roles.sql): the users `pm_migrator` (owns the schema, runs migrations) and `pm_app`
  (the API: rows only, 30 s statement timeout). It is idempotent, so it also applies a changed password and hands
  tables created by the administrator over to `pm_migrator`.
* **`up redis`** starts `pm-redis` (384 MB, [`redis/redis.conf`](redis/redis.conf), the password from
  `REDIS_PASSWORD` on the command line, never in the file).
* **`up logs`** creates the volumes `pm-loki-data` and `pm-grafana-data`, starts `pm-loki`, `pm-grafana` (anonymous
  administrator locally; dashboards in the folder *Product Management*) and `pm-server-agent` (Alloy, reads the logs
  of every container labelled `com.product-management.role=server`), then applies the cluster's log collector
  ([`logs/k8s/`](logs/k8s)) to the current `kubectl` context: Alloy in the namespace `observability`, reading every
  pod's log, and the Service `alloy-faro:12347` that receives the browser's error reports from the gateway.

The data volumes are created by `infra.sh` and declared `external` in the Compose files, so neither `infra.sh down`
nor `docker compose down -v` deletes them. To start from an empty database:
`./infra.sh down database && docker volume rm pm-postgres-data && ./infra.sh up database`.

## Passwords

Local development defaults; set any of these in the environment, or in `.env` in this folder (read by Compose,
gitignored), to use others:

| Variable | Default | Used by |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | `product_management` | the server's administrator (`product_management`): for this repository and for people only |
| `PM_MIGRATOR_PASSWORD` | `local-migrator-password` | `pm_migrator`: the backend's migration step |
| `PM_APP_PASSWORD` | `local-app-password` | `pm_app`: the API |
| `REDIS_PASSWORD` | `local-redis-password` | Redis |

The backend repository's `deploy/local-secrets.sh` uses the same defaults for the API's Secret. When you change
`PM_APP_PASSWORD` or `REDIS_PASSWORD` here, give the backend the same values (its `generated/passwords.env`, or
`FORCE=1 PM_APP_PASSWORD=... REDIS_PASSWORD=... deploy/local-secrets.sh` there), and `PM_MIGRATOR_PASSWORD` to its
`deploy/deploy.sh`.

## Logs are optional

Without the `logs` component the application works the same: the pods still log to stdout (`kubectl logs`), and the
frontend is deployed with `LOGS=off deploy/deploy.sh` so the gateway answers the browser's error reports with 204
instead of forwarding them.

**Another Loki** (e.g. Grafana Cloud) instead of the local one:

```bash
LOKI_URL=https://<host>/loki/api/v1/push LOKI_USERNAME=<user> LOKI_PASSWORD=<token> ./infra.sh up logs
```

No Loki or Grafana container starts (the Compose profile `storage` is left out); the server agent and the cluster's
collector push to `LOKI_URL`. For the collector, `infra.sh` renders `.build/logs-k8s/` (not committed) with a patch
that sets `LOKI_URL` in [`logs/k8s/alloy.yaml`](logs/k8s/alloy.yaml) (default
`http://host.docker.internal:3100/loki/api/v1/push`, the local Loki) and puts the credentials in the Secret
`loki-credentials` (after changing them: `kubectl -n observability rollout restart deployment/alloy`). Running
`./infra.sh up logs` again without `LOKI_URL` switches back. The dashboards and alert rules in
[`logs/grafana/`](logs/grafana) can be imported into that Grafana.

## What the other parts expect from this one

| Part | Expects | Where it is configured there |
| --- | --- | --- |
| backend | PostgreSQL with the database `product_management` and the users `pm_migrator`, `pm_app`; Redis with a password. Locally at `host.docker.internal:5432` / `:6379` | the connection strings in `deploy/k8s/overlays/<environment>/api.yaml`, the passwords in the Secret `api-secrets` |
| frontend | the log collector's Service `alloy-faro.observability.svc.cluster.local:12347` (optional) | `COLLECTOR_UPSTREAM` in `deploy/k8s/overlays/<environment>/gateway.yaml`, or `LOGS=off` |

Cluster add-on: [`cluster-addons.sh`](cluster-addons.sh) installs metrics-server into the current context (needed by
the API's and the gateway's autoscalers; built into k3s and most managed clusters, `microk8s enable metrics-server`
on MicroK8s).

## CI/CD (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml):

* **validate** (every change): `docker compose config` on the three Compose files, the collector's manifests rendered
  and checked with `kubeconform`, both Alloy configurations formatted as `alloy fmt` writes them.
* **deploy** (manual, on the default branch, per environment):
  * `servers:staging`, `servers:production`: a matrix over `COMPONENT` = `database`, `redis`, `logs`; each runs
    `./infra.sh up <component>` on a shell runner with Docker on that server, tagged `pm-<environment>-<component>`
    (e.g. `pm-staging-database`);
  * `cluster-logs:staging`, `cluster-logs:production`: the cluster's log collector, through the GitLab agent for
    Kubernetes (the job has `kubectl` and no Docker, so `infra.sh up logs` applies only the collector), with
    `LOKI_URL=$CLUSTER_LOKI_URL`.

CI/CD variables, scoped to the `staging` and `production` environments:

| Variable | What |
| --- | --- |
| `KUBE_CONTEXT` | `<agent configuration project path>:<agent name>` (the cluster's log collector) |
| `CLUSTER_LOKI_URL` | the Loki the cluster's collector pushes to, e.g. `http://pm-monitoring.example.internal:3100/loki/api/v1/push` |
| `LOKI_USERNAME`, `LOKI_PASSWORD` | only for a hosted Loki (masked) |
| `POSTGRES_PASSWORD` | masked, protected: the database administrator |
| `PM_MIGRATOR_PASSWORD`, `PM_APP_PASSWORD`, `REDIS_PASSWORD` | masked, protected; the same values the backend's pipeline uses: define them once as group-level variables |

Setting up the servers, the runners and TLS: [chapter 12](../docs/guide/12-from-zero.md).
