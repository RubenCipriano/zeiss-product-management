# Documentation

This is the documentation of the Product Management system: a .NET 10 API (clean architecture, EF Core, PostgreSQL,
Redis), an Angular 22 front end and an nginx gateway that verifies tokens and caches reads. The stateless parts run on
Kubernetes; PostgreSQL, Redis and the log server (Grafana and Loki, optional) run on their own servers outside the
cluster. The repository is in three self-contained parts, each meant to be its own repository: `backend/` (the API),
`frontend/` (the web app and the gateway) and `infra/` (the servers). With load tests, end-to-end tests and CI/CD
pipelines.

It is organised as a **guide in thirteen chapters**, meant to be read calmly, one at a time. Each chapter stands on its
own, takes 5 to 10 minutes, starts with what it will explain and an illustration, and ends with the source files to
open ("Where to look") and the exact commands that prove what it says ("How to verify"). The reference material
behind the guide (decision records, precise sequence diagrams, raw load-test numbers) is listed at the end.

The project's [README](../README.md) remains the quick entry point: how to run it, the API reference and the
requirements checklist. Each part has its own README on running and deploying it alone:
[backend](../backend/README.md), [frontend](../frontend/README.md), [infra](../infra/README.md).

## Reading order

**In 20 minutes** (the essentials, enough to explain the system):

1. [1. Overview](guide/01-overview.md): what it does, the pieces, the numbers.
2. [2. The request journey](guide/02-request-journey.md): one real request from the browser to the database.
3. [5. Stock and consistency](guide/05-stock-and-consistency.md): the hard requirements and how they are guaranteed.
4. Skim [11. Production readiness](guide/11-production-readiness.md): what is done and what is not.

**The full path** (about 90 minutes): chapters 1 to 11 in order. Chapters 3 to 6 build on chapter 2; chapters 7 to 11
can be read in any order after that. Chapter 12 is a runbook for standing up staging and production, to use rather
than read.

## Chapters

| # | Chapter | In one line |
| -: | --- | --- |
| 1 | [Overview](guide/01-overview.md) | The assessment in one paragraph, the users and roles, the building blocks, the three parts (infra, backend, frontend), the numbers. |
| 2 | [The request journey](guide/02-request-journey.md) | Node, pod, Service in plain words; the gateway as the only door; the headless Service; a real request with its headers. |
| 3 | [Caching](guide/03-caching.md) | Browser, gateway microcache, Redis with generations, PostgreSQL: what each keeps, for how long, what invalidates it, what it is worth. |
| 4 | [Security](guide/04-security.md) | Consumers and hashed secrets, ES256 tokens and JWKS, verification twice, permissions, rate limits, CSP, network and containers. |
| 5 | [Stock and consistency](guide/05-stock-and-consistency.md) | Unique six-digit ids, no overselling, the stock ledger, edit conflicts (412), exactly-once retries and the bug the load test found. |
| 6 | [Scaling and capacity](guide/06-scaling-and-capacity.md) | Autoscaling, why the database is the ceiling, the eight load-test scenarios, what each run found and fixed. |
| 7 | [Front end](guide/07-frontend.md) | Angular layers and rules, the product page's data flow, loading UX, accessibility, what Lighthouse and Playwright found. |
| 8 | [Logs, dashboards and alerts](guide/08-observability.md) | Logs only, and why: what is collected (the browser's errors included) and how it reaches Grafana, where it is kept and for how long, the two dashboards, twelve alerts with runbooks, one request followed by its trace id. |
| 9 | [Deployment and operations](guide/09-deployment-and-operations.md) | What runs where (cluster and servers), the three parts and their deploy scripts, the Kustomize overlays, configuration as env keys and the one Secret, migrations as `pm_migrator` before the rollout, logs as an option, the GitLab and GitHub pipelines, day-2 commands. |
| 10 | [Testing and quality](guide/10-testing-and-quality.md) | The test pyramid with counts (381 back end, 185 front end, 22 E2E), what each kind proves, the project agents. |
| 11 | [Production readiness](guide/11-production-readiness.md) | An honest ✅ / ⚠️ / ❌ assessment and the work left for go-live. |
| 12 | [From zero to production](guide/12-from-zero.md) | A runbook from an empty GitLab group and fresh servers: machines, firewall, TLS, backups, one GitLab project per part, runners, the agent, environments, every CI/CD variable per project, first deployment, day 2, go-live checklist. |
| 13 | [Reading the code](guide/13-reading-the-code.md) | Where to open the code for any question, one click followed file by file, and a plain-words glossary of the C#, Angular and nginx constructs used (`record`, `Result`, dependency injection, signals, interceptors, ETag…). |

## How to verify each claim

Everything below runs against a local Kubernetes cluster (Docker Desktop: Settings → Kubernetes → Enable). The
demo accounts are `viewer`/`viewer`, `clerk`/`clerk` and `manager`/`manager`.

```bash
kubectl config use-context docker-desktop
infra/cluster-addons.sh                               # metrics-server, once per cluster (needed by the autoscalers)
infra/infra.sh up                                     # PostgreSQL, Redis, Loki + Grafana + agents (and the log collector)
backend/deploy/deploy.sh                              # secrets (first run), API image, migrations, rollout
frontend/deploy/deploy.sh                             # web + gateway images, rollout; app on http://localhost:8080
open http://localhost:3000                            # Grafana: logs, dashboards, alerts (chapter 8)

python3 backend/scripts/check-requirements.py         # the assessment PDF: 14 checks against the running system
cd backend && dotnet test --solution ProductManagement.sln     # 381 tests (Docker running: real PostgreSQL + Redis)
cd frontend && npm ci && npm run test:ci              # 185 tests
cd frontend && npm run e2e                            # 22 Playwright tests, desktop and phone
cd frontend && npm run audit:lighthouse               # Lighthouse user flow (FORM_FACTOR=desktop for desktop)

backend/load-tests/run.sh smoke                       # every endpoint, from inside the cluster
backend/load-tests/run.sh stock-consistency           # 300 orders for 100 units: exactly 100
backend/load-tests/seed-bulk.sh && RATE=300 backend/load-tests/run.sh teams    # 850,000 products, ten teams, duplicate retries
backend/load-tests/verify-data.sh                     # data invariants: every count must be 0
```

Each chapter's "How to verify" section has the commands for its own claims.

## Reference material

The guide explains; these documents are the precise record it links to.

| Document | What it holds |
| --- | --- |
| [Architecture decision records](adr/README.md) | One short record per decision (0001–0022): context, decision, consequences. |
| [flows.md](flows.md) | Every important flow as a precise Mermaid sequence or flow diagram: caching, sign-in, the token check, the read path, stock changes, create/update, rate limiting, the product screen, deployment and CI/CD, autoscaling, load tests, logs. |
| [architecture.md](architecture.md) | The component catalogue: what runs in the cluster and on the servers, every Service, workload, Secret and policy, and the data model with its indexes. |
| [load-test-results.md](load-test-results.md) | Every load-test run, with environment, numbers, findings and the commands to reproduce them. |
| [backend/README.md](../backend/README.md), [frontend/README.md](../frontend/README.md), [infra/README.md](../infra/README.md) | Each part on its own: structure, scripts, configuration, how to run and deploy it, its CI/CD variables. |
| [images/](images) | The illustrations used by the guide. |
