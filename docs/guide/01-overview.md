# 1. Overview

**After this chapter you will know** what the system does, who uses it, which pieces it is made of, how the work is
split into three parts (infra, backend, frontend), the few numbers that describe it, and where to read next.

![Overview: users and clients reach one gateway on localhost:8080; behind it the web app and the API replicas; the API uses PostgreSQL and Redis; two autoscalers adjust the number of pods](../images/overview.svg)

---

## What the system does

The assessment asks for a product and stock management API with an Angular front end. In one paragraph:

> Products can be created, read, updated and deleted. Stock can be **decreased** and **increased by any quantity**
> (`POST /api/products/{id}/decrement-stock/{quantity}` and `.../add-to-stock/{quantity}`, both answering OK). Products
> can be found by **part of their name** and by a **stock range**. Every product has a **unique six-digit id**, with no
> duplicates even when several API instances run at once. The schema is built with **EF Core code-first migrations**
> and **seeded**. An **Angular UI** uses the API and reflects changes made through it. Everything is **tested**.

All of that is implemented, and `backend/scripts/check-requirements.py` checks it against the running system (14 checks;
see [chapter 10](10-testing-and-quality.md)). On top of the PDF, the system was built to the standard of a service
that could go to production: authentication, permissions, rate limits, caching, autoscaling, a stock ledger,
exactly-once writes and load tests. [Chapter 11](11-production-readiness.md) says honestly what is still missing.

## Who uses it

Every caller is a **registered API consumer**: a client id, a secret (stored only as a PBKDF2 hash) and one or more
roles. The local demo consumers' hashes are generated on each machine by
[`backend/deploy/local-secrets.sh`](../../backend/deploy/local-secrets.sh) (into the Secret `api-secrets`; never committed); staging and
production get theirs from the pipeline's CI/CD variables.

| Consumer | Role | What it may do | Who it represents |
| --- | --- | --- | --- |
| `viewer` / `viewer` | `viewer` | browse, search, see stock history | someone who only looks |
| `clerk` / `clerk` | `stock-clerk` | + add and remove stock | a warehouse clerk |
| `manager` / `manager` | `manager` | + create, edit and delete products | a catalogue manager |

Two kinds of client use these accounts:

* **People**, through the Angular app at http://localhost:8080. The UI hides buttons a role cannot use; the API
  enforces the same rules on every request.
* **Load tests**, k6 scripts run as a Job inside the cluster ([chapter 6](06-scaling-and-capacity.md)).

## The building blocks

The application runs in one Kubernetes namespace, `product-management`, on Docker Desktop's single-node cluster
(MicroK8s works too; k3s is the target for staging and production). Only stateless workloads and a log collector run
in the cluster. PostgreSQL, Redis and the log server (Grafana and Loki, logs only, optional) run outside it, as such
servers usually do (locally: Docker Compose containers on the same machine), so the cluster can be rebuilt from Git at any time
without touching the data ([chapter 8](08-observability.md) for the logs, [chapter 9](09-deployment-and-operations.md)
for the layout).

| Piece | What it is | Its job, in one line |
| --- | --- | --- |
| **gateway** | nginx, 2–6 pods (autoscaled) | The only door: rate limit per IP, token check, 1 s cache for API reads, security headers, routing. |
| **web** | nginx, 2 pods | Serves the built Angular app (HTML, JS, CSS). |
| **api** | ASP.NET Core (.NET 10), 2–8 pods (autoscaled) | The REST API: issues and verifies tokens, permissions, per-user limits, validation, business rules. |
| **PostgreSQL** | PostgreSQL 18 server outside the cluster, with a volume | The source of truth: products, categories, the stock ledger, the id sequence. The API connects as `pm_app`, which can read and write rows but not change the schema. |
| **Redis** | Redis 8 server outside the cluster, memory only, password protected | Shared state for the API replicas: read cache, idempotency keys, rate-limit buckets, failed sign-ins. |
| **migrations** | the API image run once with `migrate`, as the schema owner `pm_migrator` | Applies EF Core migrations and seed data once per deployment, before the rollout: from `backend/deploy/deploy.sh` locally, from a pipeline job in CI/CD. |
| **HPAs** | two HorizontalPodAutoscalers | Add or remove API and gateway pods based on CPU. |
| **logs** (optional) | Alloy in the cluster, an agent on the servers, Loki and Grafana on the log server | Every log line in one place, the browser's errors included, kept 14 days; two dashboards, twelve alerts, one trace id per request ([chapter 8](08-observability.md)). |

The back end follows clean architecture in three projects: `Core` (the domain rules and the use cases, organised by
feature, no EF Core and no HTTP) ← `Infrastructure` (EF Core, Redis) ← `Api` (HTTP), which also uses `Core` directly
([ADR 0001](../adr/0001-clean-architecture.md)). The front end is split into `core`, `shared` and `features`
([chapter 7](07-frontend.md)).

## Three parts, each deployed on its own

The repository holds three self-contained parts that would be three repositories in real life. Each has its code (or
configuration), its deploy script, its Kubernetes manifests and its own GitLab pipeline:

| Part | What it holds | Deployed by |
| --- | --- | --- |
| `infra/` | The servers outside the cluster, one Docker Compose file per component: PostgreSQL (`postgresql.conf`, the least-privilege users in `roles.sql`), Redis (`redis.conf`), and the logs (Loki, Grafana's dashboards and alert rules, the agent that ships the servers' logs, and the cluster's log collector). Locally they are containers on this machine; in production real servers or managed services. | `infra/infra.sh up [database] [redis] [logs]` |
| `backend/` | The .NET solution, its Dockerfile and tests; `deploy/` with the API's Kustomize base (the Deployment, with its configuration as environment variables, the headless Service `api`, autoscaler, disruption budget, network policy) and one overlay per environment (the connection strings and a few settings); the load tests and the requirements check. | `backend/deploy/deploy.sh` |
| `frontend/` | The Angular workspace, the web and gateway Dockerfiles and tests; `deploy/` with the web app's and the gateway's base and overlays (where the API and the log collector are, how the gateway is exposed). | `frontend/deploy/deploy.sh` |

| Image | Built from | Used by |
| --- | --- | --- |
| `product-management/api` | `backend/Dockerfile` | the API pods, and the migrations (the same image run with `migrate`) |
| `product-management/web` | `frontend/Dockerfile` | the web pods |
| `product-management/gateway` | `frontend/gateway/Dockerfile` | the gateway pods |

Each deploy script takes an environment (`ENVIRONMENT=local`, the default, `staging` or `production`); the GitLab
pipelines run the same scripts ([chapter 9](09-deployment-and-operations.md)). The API knows its data stores only
through two connection strings in its overlay, which name the servers directly and hold no password; the passwords
come from one Secret, `api-secrets`. Moving to other servers changes those two strings and the passwords, nothing
else. The logs are optional: without them the application works the same.

The contract between the API and the web app is small and made of names only:

* the namespace `product-management`;
* the Service `api`, to which the gateway sends `/api` requests and from which it downloads the API's public signing
  key (`/.well-known/jwks.json`);
* the token issuer and audience (`product-management-api`), which the gateway checks;
* the pod label `app.kubernetes.io/name: gateway`, which the API's NetworkPolicy admits.

## The numbers

| What | Value | Source |
| --- | --- | --- |
| Products in the scale dataset | 850,024 | `backend/load-tests/seed-bulk.sh` |
| Stock movements (ledger rows) | about 5 million seeded; grown to 20 million to measure the database at scale | same; [ADR 0022](../adr/0022-database-at-scale.md) |
| Deepest stock-history page of a busy product (SQL), 20 million ledger rows | 1.3–6.3 s before, 8–22 ms after (late row lookup) | [ADR 0022](../adr/0022-database-at-scale.md) |
| Product ids available | 900,000 (100000–999999, never reused) | [ADR 0002](../adr/0002-product-id-generation.md) |
| Read capacity, one 8-CPU laptop | 18,177 requests/s, 0 errors, p95 47 ms | [load-test-results.md](../load-test-results.md#five-digits-tokens-verified-at-the-gateway-microcache-shared-per-role) |
| Worst-case mix (25 % writes, reads spread over all products) | ~2,400 requests/s, 0 errors | same |
| Ten teams, 30 % of stock changes sent twice | 281 requests/s, 0 errors, p95 4.3 ms, no change applied twice | same |
| 300 simultaneous orders for 100 units | exactly 100 succeed, stock ends at 0 | [load-test-results.md](../load-test-results.md#docker-desktop-cluster-final-setup) |
| Automated tests | 381 back end, 185 front end, 22 end-to-end | [chapter 10](10-testing-and-quality.md) |

## Map of the rest of the documentation

| Chapter | Read it to understand |
| --- | --- |
| [2. The request journey](02-request-journey.md) | how one request travels from the browser to PostgreSQL and back, with real headers |
| [3. Caching](03-caching.md) | the four cache layers, what each keeps, and what a write invalidates |
| [4. Security](04-security.md) | sign-in, tokens, the double token check, permissions, rate limits, browser and network protection |
| [5. Stock and consistency](05-stock-and-consistency.md) | unique ids, no overselling, the ledger, edit conflicts, exactly-once retries |
| [6. Scaling and capacity](06-scaling-and-capacity.md) | autoscaling, why the database is the ceiling, what each load test found |
| [7. Front end](07-frontend.md) | the Angular structure, the product page's data flow, what Lighthouse and Playwright found |
| [8. Logs, dashboards and alerts](08-observability.md) | how the system is observed through its logs, and what each alert means |
| [9. Deployment and operations](09-deployment-and-operations.md) | what runs where, the three parts and their deploy scripts, the overlays, CI/CD, day-2 commands |
| [10. Testing and quality](10-testing-and-quality.md) | the test pyramid, what each layer proves, how to run it |
| [11. Production readiness](11-production-readiness.md) | what is done, what the environment must still provide |
| [12. From zero to production](12-from-zero.md) | the runbook for standing up staging and production |
| [13. Reading the code](13-reading-the-code.md) | where each answer is in the code, and what each construct means |

Reference material (decision records, precise sequence diagrams, the component catalogue, raw load-test results) is
listed in the [documentation home](../README.md#reference-material).

## Where to look

* Assessment requirements, one by one: [README "Requirements checklist"](../../README.md#requirements-checklist)
* Component catalogue and data model: [architecture.md](../architecture.md)
* All decisions: [ADR index](../adr/README.md)

## How to verify

```bash
kubectl config use-context docker-desktop
infra/infra.sh up                                      # the servers (and the log collector)
backend/deploy/deploy.sh                               # the API
frontend/deploy/deploy.sh                              # the web app and the gateway
kubectl -n product-management get pods,svc,hpa         # the pieces in the cluster
infra/infra.sh status                                  # the servers outside it
python3 backend/scripts/check-requirements.py          # the assessment PDF, 14 checks against the running system
open http://localhost:8080                             # sign in as viewer, clerk or manager
```
