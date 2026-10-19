# Documentation

This is the documentation of the Product Management system: a .NET 10 API (clean architecture, EF Core, PostgreSQL,
Redis), an Angular 22 front end and an nginx gateway that verifies tokens and caches reads. Everything runs on
Kubernetes: the application in the namespace `product-management`, and PostgreSQL, Redis and the logs (Loki, Grafana
and Alloy, optional) in the namespace `infra`, shared infrastructure that any service on the cluster can use. The
system is one GitHub repository, [zeiss-product-management](https://github.com/RubenCipriano/zeiss-product-management),
with the folders [backend/](../backend/README.md) (the API), [frontend/](../frontend/README.md) (the web app and the
gateway) and [deploy/](../deploy/README.md) (the Kubernetes manifests of every environment and the shared
infrastructure, applied by its own workflow: GitOps), next to this documentation (`docs/`) and the tools (`tools/`:
the load tests and the requirements check). With load tests, end-to-end tests and GitHub Actions workflows for each
of the three folders.

It is organised as a **guide in thirteen chapters**, meant to be read calmly, one at a time. Each chapter stands on its
own, takes 5 to 10 minutes, starts with what it will explain and an illustration, and ends with the source files to
open ("Where to look") and the exact commands that prove what it says ("How to verify"). The reference material
behind the guide (decision records, precise sequence diagrams, raw load-test numbers) is listed at the end.

The project's [README](../README.md) remains the quick entry point: how to run it, the API reference and the
requirements checklist. Each part has its own README on running and deploying it alone:
[backend](../backend/README.md), [frontend](../frontend/README.md), [deploy](../deploy/README.md).

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
| 1 | [Overview](guide/01-overview.md) | The assessment in one paragraph, the users and roles, the building blocks, the three parts (deploy, backend, frontend), the numbers. |
| 2 | [The request journey](guide/02-request-journey.md) | Node, pod, Service in plain words; the gateway as the only door; the headless Service; a real request with its headers. |
| 3 | [Caching](guide/03-caching.md) | Browser, gateway microcache, Redis with generations, PostgreSQL: what each keeps, for how long, what invalidates it, what it is worth. |
| 4 | [Security](guide/04-security.md) | Consumers and hashed secrets, ES256 tokens and JWKS, verification twice, permissions, rate limits, CSP, network and containers. |
| 5 | [Stock and consistency](guide/05-stock-and-consistency.md) | Unique six-digit ids, no overselling, the stock ledger, edit conflicts (412), exactly-once retries and the bug the load test found. |
| 6 | [Scaling and capacity](guide/06-scaling-and-capacity.md) | Autoscaling, why the database is the ceiling, the eight load-test scenarios, what each run found and fixed. |
| 7 | [Front end](guide/07-frontend.md) | Angular layers and rules, the product page's data flow, loading UX, accessibility, what Lighthouse and Playwright found. |
| 8 | [Logs, dashboards and alerts](guide/08-observability.md) | Logs only, and why: what is collected (the browser's errors included) and how it reaches Grafana, where it is kept and for how long, the two dashboards, twelve alerts with runbooks, one request followed by its trace id. |
| 9 | [Deployment and operations](guide/09-deployment-and-operations.md) | What runs where (the namespaces `product-management` and `infra`), the three folders and the deploy scripts, one Kustomize folder per environment, configuration as env keys and the Secrets, migrations applied by the API itself at startup (as `pm_migrator`, one replica at a time), logs as an option, the GitHub Actions workflows (releases pulled into `deploy/` as commits, rollback as `git revert`), day-2 commands. |
| 10 | [Testing and quality](guide/10-testing-and-quality.md) | The test pyramid with counts (390 back end, 187 front end, 22 E2E), what each kind proves, the project agents. |
| 11 | [Production readiness](guide/11-production-readiness.md) | An honest ✅ / ⚠️ / ❌ assessment and the work left for go-live. |
| 12 | [From zero to production](guide/12-from-zero.md) | A runbook from an empty GitHub repository and fresh machines: k3s nodes and their roles, firewall, TLS, backups, the repository's settings, the GitHub environments and every secret and variable, first deployment, day 2, go-live checklist. |
| 13 | [Reading the code](guide/13-reading-the-code.md) | Where to open the code for any question, one click followed file by file, and a plain-words glossary of the C#, Angular and nginx constructs used (`record`, `Result`, dependency injection, signals, interceptors, ETag…). |

## How to verify each claim

Everything below runs against a local Kubernetes cluster (Docker Desktop: Settings → Kubernetes → Enable). The
demo accounts are `viewer`/`viewer`, `clerk`/`clerk` and `manager`/`manager`.

```bash
kubectl config use-context docker-desktop
deploy/scripts/infra.sh up                            # namespace infra: PostgreSQL, Redis, Loki + Grafana + Alloy
                                                      # (and metrics-server, if the cluster has none)
deploy/scripts/deploy.sh local                        # secrets and database (first run), api + web + gateway images,
                                                      # rollout (the API migrates); app on http://localhost:8080
kubectl diff -k deploy/environments/local             # no drift: the cluster is what was applied
kubectl -n infra port-forward svc/grafana 3000:3000 & # then http://localhost:3000: logs, dashboards, alerts (chapter 8)
kubectl -n infra port-forward svc/loki 3100:3100 &    # Loki, for the e2e test that reads a browser error back

python3 tools/check-requirements.py                   # the assessment PDF: 14 checks against the running system
cd backend && dotnet test --solution ProductManagement.sln     # 390 tests (Docker running: real PostgreSQL + Redis)
cd frontend && npm ci && npm run test:ci              # 187 tests
cd frontend && npm run e2e                            # 22 Playwright tests, desktop and phone
cd frontend && npm run audit:lighthouse               # Lighthouse user flow (FORM_FACTOR=desktop for desktop)

tools/load-tests/run.sh smoke                         # every endpoint, from inside the cluster
tools/load-tests/run.sh stock-consistency             # 300 orders for 100 units: exactly 100
tools/load-tests/seed-bulk.sh && RATE=300 tools/load-tests/run.sh teams    # 850,000 products, ten teams, duplicate retries
tools/load-tests/verify-data.sh                       # data invariants: every count must be 0
```

Each chapter's "How to verify" section has the commands for its own claims.

## Reference material

The guide explains; these documents are the precise record it links to.

| Document | What it holds |
| --- | --- |
| [Architecture decision records](adr/README.md) | One short record per decision (0001–0022): context, decision, consequences. |
| [flows.md](flows.md) | Every important flow as a precise Mermaid sequence or flow diagram: caching, sign-in, the token check, the read path, stock changes, create/update, rate limiting, the product screen, deployment and CI/CD, autoscaling, load tests, logs. |
| [architecture.md](architecture.md) | The component catalogue: what runs in each namespace of the cluster, every Service, workload, Secret and policy, and the data model with its indexes. |
| [load-test-results.md](load-test-results.md) | Every load-test run, with environment, numbers, findings and the commands to reproduce them. |
| [backend/README.md](../backend/README.md), [frontend/README.md](../frontend/README.md), [deploy/README.md](../deploy/README.md) | Each folder on its own: structure, scripts, configuration, how to run, release or deploy it, its workflows, secrets and variables. |
| [images/](images) | The illustrations used by the guide. |
