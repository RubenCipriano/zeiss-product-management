# 0011 — Kubernetes deployment (Docker Desktop or MicroK8s locally), one manifest per project

**Status:** accepted (supersedes the Docker Compose setup)

## Context

The system must run with several API replicas to prove it behaves correctly when scaled out (unique ids, atomic
stock, shared cache, sessions), and the frontend and backend live in separate repositories with independent
deployments.

## Decision

* **Kubernetes**, run locally on **Docker Desktop's built-in cluster** (everything is visible in Docker Desktop, and
  the gateway is published on `localhost:8080`) or on **MicroK8s** (`scripts/cluster-up.sh` creates a Multipass VM
  on macOS/Windows; native on Linux; also used in CI). The manifests are plain Kubernetes and also work on AKS, EKS
  or GKE. The deploy scripts target the current `kubectl` context. On Docker Desktop, images built locally are used
  directly; on MicroK8s they are pushed to its registry.
* **One self-contained YAML per project**, each with its own deploy script:
  * `backend/deploy/kubernetes.yaml`: PostgreSQL (StatefulSet + PVC), Redis, the API's signing key and consumers
    (Secrets), the migration Job, the API (Deployment, `api` and headless `api-headless` Services,
    PodDisruptionBudget) and NetworkPolicies.
  * `frontend/deploy/kubernetes.yaml`: the web app and the gateway (`LoadBalancer`), with PodDisruptionBudgets.
  * The **contract** between them is just names: the namespace, the Service `api-headless`, and the `gateway` pod
    label. Scaling the API (`kubectl scale deployment/api --replicas=4`) needs nothing from the
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
  the web app accept only the gateway. They are enforced by CNIs that support them (MicroK8s' Calico, cloud CNIs);
  Docker Desktop's single-node cluster accepts but does not enforce them.

## Consequences

* Every build gets an immutable tag (commit + timestamp for uncommitted changes); the deploy script substitutes it
  into the manifest. A real pipeline pushes those tags to a registry and only runs `kubectl apply` (or GitOps).
* Secrets in the manifests are development values. Production uses External Secrets, Sealed Secrets or Vault.
* PostgreSQL in-cluster is fine for development. Production would use a managed database (Azure Database for
  PostgreSQL, RDS) or an operator such as CloudNativePG, with backups.
* An optional HorizontalPodAutoscaler can manage API replicas (metrics-server is enabled). It is left out of the
  manifest so manual scaling, as in the load tests, is not overridden.
