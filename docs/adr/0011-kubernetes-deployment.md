# 0011 — Kubernetes deployment (MicroK8s locally), one manifest per project

**Status:** accepted (supersedes the Docker Compose setup)

## Context

The system must run with several API replicas to prove it behaves correctly when scaled out (unique ids, atomic
stock, shared cache, sessions), and the frontend and backend live in separate repositories with independent
deployments.

## Decision

* **Kubernetes**, run locally with **MicroK8s** (`scripts/cluster-up.sh` creates a Multipass VM on macOS/Windows;
  on Linux MicroK8s installs natively). The manifests are plain Kubernetes and also work on AKS, EKS or GKE.
* **One self-contained YAML per project**, each with its own deploy script:
  * `backend/deploy/kubernetes.yaml`: PostgreSQL (StatefulSet + PVC), Redis, Keycloak, the migration Job, the API
    (Deployment, `api` and headless `api-headless` Services, PodDisruptionBudget) and NetworkPolicies.
  * `frontend/deploy/kubernetes.yaml`: the web app and the gateway (NodePort), with PodDisruptionBudgets.
  * The **contract** between them is just names: the namespace, the Services `api-headless` and `keycloak`, and the
    `gateway` pod label. Scaling the API (`kubectl scale deployment/api --replicas=4`) needs nothing from the
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
  the web app accept only the gateway; Keycloak accepts the gateway, the API and the migration Job.

## Consequences

* Local images are loaded into MicroK8s (`ctr image import`) with a fixed `:local` tag and a rollout restart. A real
  pipeline pushes immutable, versioned tags to a registry and only runs `kubectl apply`.
* Secrets in the manifests are development values. Production uses External Secrets, Sealed Secrets or Vault.
* PostgreSQL in-cluster is fine for development. Production would use a managed database (Azure Database for
  PostgreSQL, RDS) or an operator such as CloudNativePG, with backups.
* An optional HorizontalPodAutoscaler can manage API replicas (metrics-server is enabled). It is left out of the
  manifest so manual scaling, as in the load tests, is not overridden.
