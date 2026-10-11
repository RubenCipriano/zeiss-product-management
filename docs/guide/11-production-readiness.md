# 11. Production readiness

**After this chapter you will know** how the system measures up against the assessment PDF, the quality expectations
given with it, and what a production service needs, with the evidence for each claim and an honest list of what is
still missing.

Every ✅ points to evidence that can be re-run. ⚠️ means "done in the code, but the target environment must provide
something" or "partly done"; ❌ means missing.

**Verdict:** the application is production-ready as software: every requirement is implemented and checked
automatically, it is secure by default, scales horizontally, stays correct under concurrency and overload, and has
been measured at load. The deployment is written for staging and production too (one Kustomize folder per environment in a
deploy repository applied by its own GitLab pipeline, shared PostgreSQL, Redis and logs in the cluster's `infra` namespace, least-privilege database users, logs
with alerts), with a runbook in [chapter 12](12-from-zero.md). Going live still needs the environment items in
[Before go-live](#before-go-live) (backups shipped off the node, the pipeline run for real, a secret manager, a
multi-node cluster whose network plugin enforces NetworkPolicies); none of them requires application code changes.

- [1. The assessment PDF](#1-the-assessment-pdf)
- [2. Quality expectations](#2-quality-expectations)
- [3. Production checklist](#3-production-checklist)
- [Before go-live](#before-go-live)
- [How to verify everything](#how-to-verify-everything)

## 1. The assessment PDF

Checked against the running system by `tools/check-requirements.py` (14 checks, all passing; run by hand, also against staging with `BASE_URL`).

| Requirement | Status | Evidence |
| --- | :---: | --- |
| CRUD: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` | ✅ | check-requirements (5 checks); `ProductCrudTests`; E2E `products.spec.ts` |
| `POST …/decrement-stock/{quantity}` returns OK after decrementing by the quantity | ✅ | 35 − 7 = 28 (200); refuses to go below zero (409) |
| `POST …/add-to-stock/{quantity}` returns OK after incrementing by the quantity | ✅ | 10 + 25 = 35 (200); UI steps ±1/5/10/25/100 and any amount in *Adjust stock* |
| `GET …/search?name=` with partial matches | ✅ | case-insensitive partial match; trigram index |
| `GET …/stock-level?min=&max=` | ✅ | only products in range; index `(stock_quantity, name, id)` |
| Unique 6-digit id, no duplicates with multiple instances | ✅ | PostgreSQL sequence `100000–999999`; 40 concurrent creates over the replicas → 40 unique ids; 850,024 products unique ([ADR 0002](../adr/0002-product-id-generation.md), [chapter 5](05-stock-and-consistency.md)) |
| Validation of create and update | ✅ | 400 with the invalid fields named; domain guards; database CHECKs |
| Stock in every list response | ✅ | checked on all three list endpoints |
| EF Core code-first migrations | ✅ | `Persistence/Migrations`, applied by the API itself when it starts, before it serves, as the schema owner `pm_migrator`, one replica at a time (a PostgreSQL advisory lock); the same in every environment; large indexes built `CONCURRENTLY` |
| Database seeding | ✅ | sample data via `UseSeeding`; bulk data via `tools/load-tests/seed-bulk.sh` |
| Angular frontend consuming the API, reflecting changes made through the API | ✅ | every endpoint used; background refresh; E2E suite ([chapter 7](07-frontend.md)) |
| Unit tests for various scenarios; BDD optional | ✅ | 390 back-end tests (219 unit, 155 integration, 16 Reqnroll BDD scenarios); 187 front-end tests; 22 E2E ([chapter 10](10-testing-and-quality.md)) |
| Documentation for running locally | ✅ | [README](../../README.md) "Run it"; this guide |

## 2. Quality expectations

| Expectation | Status | Where |
| --- | :---: | --- |
| Clean architecture, thin controllers | ✅ | [ADR 0001](../adr/0001-clean-architecture.md); front-end layers data-access / feature / ui ([ADR 0007](../adr/0007-frontend-state.md)) |
| SOLID, reviewed | ✅ | back-end review applied: the migrations at startup use their own short-lived connection with the migrator's credentials, never the request pool; every options class is validated at start-up (`ValidateOnStart`); the API refuses to start without Redis outside Development, instead of silently running with per-replica idempotency and limits |
| Decisions, assumptions and trade-offs documented | ✅ | the [ADRs](../adr/README.md); README "Assumptions" and "Trade-offs"; this guide |
| API design, validation, error handling | ✅ | RFC 9457 problem details with stable `errorCode`s ([ADR 0005](../adr/0005-error-handling.md)) |
| EF Core best practices, concurrency | ✅ | no-tracking projections, `xmin` + ETag/If-Match, atomic stock updates, idempotency in the database |
| Meaningful tests | ✅ | concurrency, idempotency across replicas, security matrix, cache behaviour, E2E per role |
| Consistent naming, structure, DTOs | ✅ | reviewed by the architecture review |

## 3. Production checklist

### Security

| Item | Status | Notes |
| --- | :---: | --- |
| Authentication | ✅ | ES256 JWTs, public key via JWKS, verified at the gateway and again by the API ([ADR 0009](../adr/0009-authentication-authorization.md), [ADR 0020](../adr/0020-edge-token-verification.md), [chapter 4](04-security.md)) |
| Authorization | ✅ | permission policies, secure by default; 30-case role × operation matrix |
| Brute force, rate limiting | ✅ | failed sign-ins counted cluster-wide; per-user limits shared by all replicas ([ADR 0010](../adr/0010-rate-limiting.md)) |
| Exactly-once writes | ✅ | Idempotency-Key + unique request key in the ledger ([ADR 0017](../adr/0017-idempotency-keys.md)) |
| Browser security | ✅ | strict CSP, no third-party requests, COOP/CORP, Permissions-Policy ([ADR 0019](../adr/0019-content-security-policy.md)) |
| Fail fast on bad configuration | ✅ | signing key, consumers and every options section validated at start-up; no Redis outside Development → the API does not start |
| Database privileges | ✅ | the API's user `pm_app` reads and writes rows only (`CREATE`, `ALTER`, `DROP` refused), 30 s statement timeout; only the API's migration step at startup (its own unpooled connection, closed before it serves) uses the schema owner `pm_migrator`; users from [`create-database.sh`](../../deploy/scripts/create-database.sh), grants from the migration [`GrantAppPermissions`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations/20261010164241_GrantAppPermissions.cs) |
| Network | ⚠️ | NetworkPolicies: the API and the web pods accept connections only from the gateway. **Not enforced on Docker Desktop** (its network plugin ignores them; verified: a web pod reaches an API pod). Enforced on k3s and on production clusters with Calico or Cilium. PostgreSQL and Redis (namespace `infra`) are reachable only inside the cluster and protected by random credentials; between nodes only the k3s ports are open ([chapter 12](12-from-zero.md)). |
| Containers | ⚠️ | api, web: non-root, read-only root filesystem, all capabilities dropped, `RuntimeDefault` seccomp. gateway: the same, but with a writable root filesystem (nginx renders its configuration at start-up). |
| Secrets | ⚠️ | no password in any YAML, connection string or CI/CD variable: the database and Redis passwords are random and live only in the cluster (`database-credentials` from `deploy/scripts/create-database.sh`, `redis-credentials` copied by the deploy); the API's own keys in `api-secrets`, locally generated on each machine (`deploy/scripts/local-secrets.sh`, gitignored), staging and production from the deploy project's protected CI/CD variables (`deploy/scripts/deploy.sh`; the deploy pipeline fails if a Secret appears in their environment folders). Next: a secret manager (External Secrets / Vault) |
| TLS | ⚠️ | at the edge: the staging and production environments have an Ingress with a Let's Encrypt certificate from cert-manager (then add HSTS). Inside the cluster (API to PostgreSQL and Redis): plain TCP on the pod network, optional; a database or cache outside the cluster would get `SSL Mode=VerifyFull` with the internal CA in the API pods ([chapter 12](12-from-zero.md)) |
| Client IPs at the edge | ⚠️ | behind the Ingress the gateway trusts the controller's `X-Forwarded-For` (`GATEWAY_TRUSTED_PROXIES`); the controller's own load balancer must preserve client addresses (`externalTrafficPolicy: Local` / PROXY protocol) |
| Image supply chain | ⚠️ | images built and deployed by immutable tag; production: a registry with vulnerability scanning and signing |

### Reliability and correctness

| Item | Status | Notes |
| --- | :---: | --- |
| No overselling, no lost updates | ✅ | conditional UPDATE; 300 orders for 100 units → exactly 100 |
| No duplicates under overload | ✅ | found by a capacity test, fixed in the database; every run since exact |
| Ledger always matches stock | ✅ | `verify-data.sh` on 850,000 products after every load test |
| Graceful degradation | ✅ | Redis down → cache and limits fall back; idempotency refuses (503) rather than risk duplicates |
| Health probes | ✅ | readiness tolerant of a slow database, its check on a connection of its own so a saturated pool never fails every replica at once (no cascading outage), liveness, startup |
| Zero-downtime deploys | ✅ | rolling updates with `maxUnavailable: 0`, preStop, PodDisruptionBudgets |
| Database backups / point-in-time recovery | ⚠️ | a nightly `pg_dump` of every database (CronJob `postgres-backup`, 7 days kept) onto a volume on the same node: it protects against mistakes, not against losing the node. Production ships the dumps off the node (object storage) or uses volume snapshots; point-in-time recovery and a standby need an operator such as CloudNativePG ([ADR 0006](../adr/0006-postgresql.md), [chapter 12](12-from-zero.md)). The API reaches the database only by name, so replacing it changes the connection string in the environment's `api.yaml` and nothing else |
| Redis high availability | ⚠️ | a single instance here; correctness never depends on it, but production would use a replicated Redis |

### Scalability and performance

| Item | Status | Notes |
| --- | :---: | --- |
| Horizontal scaling | ✅ | HPA for the API (2–8) and the gateway (2–6) ([chapter 6](06-scaling-and-capacity.md)) |
| Data at scale | ✅ | 850,000 products and a 20-million-row ledger; every index serves a query and is pinned by `DatabaseAtScaleTests`; deep history pages 1.3–6.3 s → 8–22 ms; per-table autovacuum ([ADR 0022](../adr/0022-database-at-scale.md)) |
| Capacity | ✅ | one 8-CPU laptop: 18,177 req/s of reads, ~2,400 req/s worst-case mix, 0 errors ([results](../load-test-results.md)) |
| Caching | ✅ | four layers, each measured ([chapter 3](03-caching.md)) |
| Frontend performance | ✅ | Lighthouse: Performance 97–100, Accessibility 100, Best Practices 100 (mobile and desktop) |

### Observability

| Item | Status | Notes |
| --- | :---: | --- |
| Structured logs | ✅ | JSON logs, source-generated log messages, trace id in every problem response |
| Logs of every component in one place | ✅ | every pod in every namespace, PostgreSQL and Redis included (Alloy) → Loki → Grafana, in the namespace `infra` (optional, or another Loki through `LOKI_URL`); one trace id from the gateway through the API ([chapter 8](08-observability.md)) |
| Browser errors | ✅ | errors only, with Grafana Faro (lazy chunk, nothing at first paint) → gateway `/collect` → Alloy → Loki; route without query string, release, browser, user id; never the token, form values or a session id; an alert and a panel ([chapter 8](08-observability.md#browser-errors)) |
| Log retention | ✅ | 14 days in Loki (compactor), the same for every source; production sets the company's period on object storage |
| Dashboards and alerts | ⚠️ | two dashboards (*Product Management*: the numbers; *Logs*: every source's lines side by side) and 12 LogQL alert rules (errors, latency, traffic, rate limits, database, cache, browser errors), each with a runbook; notifications go to a **placeholder** webhook until an on-call channel is configured |
| Traces and metrics | ❌ | not collected, by decision (also not from the browser: no performance timings or web vitals): the access log holds rate, errors, latency and cache hits; how to add them is in [chapter 8](08-observability.md#what-we-would-add-next) |

### Delivery

| Item | Status | Notes |
| --- | :---: | --- |
| CI | ⚠️ | one GitLab pipeline per repository: build with warnings as errors, `dotnet format`, all test suites, lint, image scan (backend, frontend); every environment's manifests validated, shellcheck, the Alloy configuration (deploy); the PDF check and Playwright are run by hand against an environment; defined and validated against GitLab's CI schema, **not yet run on a GitLab instance** ([chapter 9](09-deployment-and-operations.md#cicd-pipelines)) |
| CD to staging and production | ⚠️ | GitOps with a pull model: backend and frontend (test → build → scan; a version tag publishes `X.Y.Z` and a GitLab Release), deploy (validate → `update:staging` on a schedule → `deploy:staging` and smoke check → `propose:production` merge request → manual `deploy:production`; manual infra jobs; the new API pods migrate the database as they start); rollback is `set-version.sh` with an older version; **not yet run on a GitLab instance** ([chapter 12](12-from-zero.md)) |
| Database migrations | ✅ | applied by each new API version when its first pod starts, as `pm_migrator`, under an advisory lock (two instances starting together on an empty database: tested); a failure keeps the new pods unready while the old ones serve; large indexes built concurrently; expand/contract, never rolled back |
| Independent deployment of each part | ✅ | the API and the web app are released on their own (each pipeline changes only its own image tags in the deploy repository); the shared infrastructure separately: `deploy/scripts/deploy.sh <environment> apps` or `infra`, locally `deploy/scripts/infra.sh up [postgres] [redis] [logs]` and `deploy/scripts/deploy.sh local` ([chapter 9](09-deployment-and-operations.md#the-layout-three-parts)) |
| Environments | ✅ | one Kustomize folder each (`deploy/environments/local`, `staging`, `production`); the differences fit in one short file per environment |
| GitOps | ⚠️ | the deploy repository is the only record of what runs (one commit per release, `git revert` to roll back) and only its pipeline changes the clusters, with `kubectl apply`; Argo CD pointed at `deploy/environments/<environment>` is the next step (pruning, drift detection) |

## Before go-live

Environment work, in order, following the runbook in [chapter 12](12-from-zero.md):

1. **Data:** the nightly dumps shipped off the node (object storage) or volume snapshots, and a timed restore drill;
   for point-in-time recovery and a standby, PostgreSQL under an operator (CloudNativePG); the logs on a Loki with
   object storage and the company's retention (or outside the cluster through `LOKI_URL`), Grafana behind SSO and
   alerts routed to on-call.
2. **GitLab:** the agent for Kubernetes (or a `KUBECONFIG`), protected environments, protected tags `v*` in backend
   and frontend, the deploy project's CI/CD variables (pushing with the job's own token for
   `update:staging`) and the `update:staging` schedule; then a first version released through staging and a rehearsed
   rollback (`deploy/scripts/set-version.sh` with the older version). The shared runners are enough: every job reaches the
   cluster through `kubectl`.
3. **TLS:** the certificate for the public names and HSTS; inside the cluster TLS is optional (a database or cache
   outside it would need `SSL Mode=VerifyFull` and the internal CA in the API pods).
4. **Secrets** from a secret manager; a new signing key and consumer secrets per environment (never the local ones).
5. **Multi-node cluster** (k3s, one or three servers for the control plane, and `deploy/scripts/add-node.sh` for nodes with
   the roles database, cache and apps) with a network plugin that enforces NetworkPolicies, Pod Security admission
   (`restricted`), a replicated Redis, and the HPAs' limits re-measured on the real nodes.
6. **Client IPs** preserved by the load balancer in front of the Ingress controller; Argo CD (prepared in
   `deploy/argocd/`: Applications, Image Updater; no migration hook needed).

## How to verify everything

```bash
deploy/scripts/infra.sh up                 # PostgreSQL, Redis and the logs, in the namespace infra
deploy/scripts/deploy.sh local             # database (first run), the API (it migrates), the web app and the gateway
python3 tools/check-requirements.py        # the PDF, 14 checks
kubectl -n infra port-forward svc/grafana 3000:3000 &   # then http://localhost:3000: Grafana, dashboards, alert rules
cd frontend && npm run e2e                 # Playwright, desktop and mobile
cd frontend && npm run audit:lighthouse    # Lighthouse user flow (FORM_FACTOR=desktop for desktop)
tools/load-tests/verify-data.sh            # data invariants
```


