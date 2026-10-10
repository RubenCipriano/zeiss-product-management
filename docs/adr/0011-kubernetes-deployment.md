# 0011 — Kubernetes deployment (Docker Desktop locally), one manifest per project

**Status:** superseded in part by [0021](0021-deployment-layout.md) (supersedes the Docker Compose setup)

> **Update (2026-10-10):** the per-project manifests and deploy scripts, PostgreSQL and Redis in the cluster, the
> migration Job and the `api` ClusterIP Service are gone: see [0021](0021-deployment-layout.md) (Kustomize overlays,
> at first servers outside the cluster, migrations run by the pipeline with the API image). Later PostgreSQL, Redis
> and the logs came back into the cluster, as shared infrastructure in their own namespace `infra` (now in the
> deploy repository: `deploy/infra/`, applied by `deploy/scripts/infra.sh`), and the migrations run as a one-off pod
> started with `kubectl run` (by the backend pipeline's `ci/migrate.sh`; locally by `deploy/scripts/deploy.sh local`),
> still without a Job manifest. Still valid: Kubernetes on Docker
> Desktop locally, one Dockerfile per image, pod hardening, zero-downtime rollouts, NetworkPolicies between the
> gateway and the applications; the HorizontalPodAutoscalers were added later ([0018](0018-autoscaling-and-scale-testing.md)).
> The second local cluster option and its VM scripts (`scripts/cluster-up.sh`, `cluster-down.sh`) were removed:
> Docker Desktop is the only local cluster, and any other cluster takes images from a registry. Later the deployment was split into three parts (infra, backend, frontend) and the
> headless Service `api-headless` renamed to `api` ([0021, update](0021-deployment-layout.md#update-2026-10-10));
> finally every manifest and environment moved into one deploy repository (`deploy/apps/`, `deploy/environments/`),
> whose pipeline is the only one that applies, while the app pipelines release by committing image tags to it
> ([0021, revision](0021-deployment-layout.md#revision-2026-10-10-gitops-with-a-deploy-repository)).

## Context

The system must run with several API replicas to prove it behaves correctly when scaled out (unique ids, atomic
stock, shared cache, sessions), and the frontend and backend live in separate repositories with independent
deployments.

## Decision

* **Kubernetes**, run locally on **Docker Desktop's built-in cluster** (everything is visible in Docker Desktop, and
  the gateway is published on `localhost:8080`). The manifests are plain Kubernetes and also work on k3s, AKS, EKS
  or GKE. The deploy scripts target the current `kubectl` context. On Docker Desktop, images built locally are used
  directly; other clusters pull them from a registry.
* **One self-contained YAML per project**, each with its own deploy script (both files removed since; see the update
  above):
  * `backend/deploy/kubernetes.yaml`: PostgreSQL (StatefulSet + PVC), Redis, the API's signing key and consumers
    (Secrets), the migration Job, the API (Deployment, `api` and headless `api-headless` Services,
    PodDisruptionBudget) and NetworkPolicies.
  * `frontend/deploy/kubernetes.yaml`: the web app and the gateway (`LoadBalancer`), with PodDisruptionBudgets.
  * The **contract** between them is just names: the namespace, the Service `api-headless`, and the `gateway` pod
    label. Scaling the API (its HPA, or `kubectl patch hpa api`) needs nothing from the
    frontend.
* **One Dockerfile per project** (`backend/Dockerfile`, `frontend/Dockerfile`, `frontend/gateway/Dockerfile`). All
  images are multi-stage and run as non-root.
* **Migrations run once per deployment in a Job** (`dotnet ProductManagement.Api.dll migrate`), not on every
  replica start. The deploy script waits for the Job before rolling out the API.
* **Pod hardening:** `runAsNonRoot`, `readOnlyRootFilesystem` (API, web, Redis; `emptyDir` for `/tmp`), all
  capabilities dropped, no privilege escalation, `RuntimeDefault` seccomp profile, CPU/memory requests and limits.
* **Zero-downtime rollouts:** `maxUnavailable: 0`, startup/readiness/liveness probes (readiness includes PostgreSQL;
  Redis only degrades it), and a `preStop` delay so in-flight requests finish before a pod leaves the endpoints.
* **NetworkPolicies:** PostgreSQL accepts only the API and the migration Job; Redis accepts only those two; the API and
  the web app accept only the gateway. They are enforced by CNIs that support them (Calico, Cilium, cloud CNIs);
  Docker Desktop's single-node cluster accepts but does not enforce them.

## Consequences

* Every build gets an immutable tag (commit + timestamp for uncommitted changes); the deploy script substitutes it
  into the manifest. A real pipeline pushes those tags to a registry and only runs `kubectl apply` (or GitOps).
* Secrets in the manifests are development values. Production uses External Secrets, Sealed Secrets or Vault.
* PostgreSQL in-cluster is fine for development. Production would use a managed database (Azure Database for
  PostgreSQL, RDS) or an operator such as CloudNativePG, with backups.
* An optional HorizontalPodAutoscaler can manage API replicas (metrics-server is enabled). It is left out of the
  manifest so manual scaling, as in the load tests, is not overridden.
