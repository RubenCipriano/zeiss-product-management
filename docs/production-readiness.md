# Production readiness

An honest assessment against the assessment PDF, the quality expectations given with it, and what a production
service needs. Every ✅ points to evidence that can be re-run. ⚠️ means "done in the code, but the target environment
must provide something"; ❌ means missing.

**Verdict:** the application is production-ready as software: every requirement is implemented and checked
automatically, it is secure by default, scales horizontally, stays correct under concurrency and overload, and has
been measured at load. Going live still needs the environment items in [Before go-live](#before-go-live) (TLS, a
secret manager, managed PostgreSQL with backups, a multi-node cluster); none of them requires code changes.

- [1. The assessment PDF](#1-the-assessment-pdf)
- [2. Quality expectations](#2-quality-expectations)
- [3. Production checklist](#3-production-checklist)
- [Before go-live](#before-go-live)
- [How to verify everything](#how-to-verify-everything)

## 1. The assessment PDF

Checked against the running system by `scripts/check-requirements.py` (14 checks, all passing; also in CI).

| Requirement | Status | Evidence |
| --- | :---: | --- |
| CRUD: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` | ✅ | check-requirements (5 checks); `ProductCrudTests`; E2E `products.spec.ts` |
| `POST …/decrement-stock/{quantity}` returns OK after decrementing by the quantity | ✅ | 35 − 7 = 28 (200); refuses to go below zero (409) |
| `POST …/add-to-stock/{quantity}` returns OK after incrementing by the quantity | ✅ | 10 + 25 = 35 (200); UI steps ±1/5/10/25/100 and any amount in *Adjust stock* |
| `GET …/search?name=` with partial matches | ✅ | case-insensitive partial match; trigram index |
| `GET …/stock-level?min=&max=` | ✅ | only products in range; index `(stock_quantity, name, id)` |
| Unique 6-digit id, no duplicates with multiple instances | ✅ | PostgreSQL sequence `100000–999999`; 40 concurrent creates over the replicas → 40 unique ids; 850,024 products unique ([ADR 0002](adr/0002-product-id-generation.md)) |
| Validation of create and update | ✅ | 400 with the invalid fields named; domain guards; database CHECKs |
| Stock in every list response | ✅ | checked on all three list endpoints |
| EF Core code-first migrations | ✅ | `Persistence/Migrations`, applied by the `db-migrate` Job (large indexes built `CONCURRENTLY`) |
| Database seeding | ✅ | sample data via `UseSeeding`; bulk data via `load-tests/seed-bulk.sh` |
| Angular frontend consuming the API, reflecting changes made through the API | ✅ | every endpoint used; background refresh; E2E suite |
| Unit tests for various scenarios; BDD optional | ✅ | backend unit + integration + Reqnroll BDD; 107 frontend tests; 20 E2E |
| Documentation for running locally | ✅ | [README](../README.md) "Run it" |

## 2. Quality expectations

| Expectation | Status | Where |
| --- | :---: | --- |
| Clean architecture, thin controllers | ✅ | [ADR 0001](adr/0001-clean-architecture.md); frontend layers data-access / feature / ui ([ADR 0007](adr/0007-frontend-state.md)) |
| Decisions, assumptions and trade-offs documented | ✅ | 20 ADRs; README "Assumptions" and "Trade-offs" |
| API design, validation, error handling | ✅ | RFC 9457 problem details with stable `errorCode`s ([ADR 0005](adr/0005-error-handling.md)) |
| EF Core best practices, concurrency | ✅ | no-tracking projections, `xmin` + ETag/If-Match, atomic stock updates, idempotency in the database |
| Meaningful tests | ✅ | concurrency, idempotency across replicas, security matrix, cache behaviour, E2E per role |
| Consistent naming, structure, DTOs | ✅ | reviewed by an architecture review (`.claude/agents/architecture-reviewer.md`) |

## 3. Production checklist

### Security

| Item | Status | Notes |
| --- | :---: | --- |
| Authentication | ✅ | ES256 JWTs, public key via JWKS, verified at the gateway and again by the API ([ADR 0009](adr/0009-authentication-authorization.md), [ADR 0020](adr/0020-edge-token-verification.md)) |
| Authorization | ✅ | permission policies, secure by default; 30-case role × operation matrix |
| Brute force, rate limiting | ✅ | failed sign-ins counted cluster-wide; per-user limits shared by all replicas ([ADR 0010](adr/0010-rate-limiting.md)) |
| Exactly-once writes | ✅ | Idempotency-Key + unique request key in the ledger ([ADR 0017](adr/0017-idempotency-keys.md)) |
| Browser security | ✅ | strict CSP, no third-party requests, COOP/CORP, Permissions-Policy ([ADR 0019](adr/0019-content-security-policy.md)) |
| Network | ✅ | NetworkPolicies: database and Redis reachable only from the API |
| Containers | ✅ | non-root, read-only root filesystem, no capabilities, seccomp |
| Secrets | ⚠️ | in the repository as **local-development values**; production: a secret manager (External Secrets / Vault) and new keys |
| TLS | ⚠️ | terminated in front of the gateway by the platform (ingress / load balancer); then add HSTS |
| Image supply chain | ⚠️ | images built and deployed by tag; production: a registry with vulnerability scanning and signing |

### Reliability and correctness

| Item | Status | Notes |
| --- | :---: | --- |
| No overselling, no lost updates | ✅ | conditional UPDATE; 300 orders for 100 units → exactly 100 |
| No duplicates under overload | ✅ | found by a capacity test, fixed in the database; every run since exact |
| Ledger always matches stock | ✅ | `verify-data.sh` on 850,000 products after every load test |
| Graceful degradation | ✅ | Redis down → cache and limits fall back; idempotency refuses (503) rather than risk duplicates |
| Health probes | ✅ | readiness tolerant of a slow database (no cascading outage), liveness, startup |
| Zero-downtime deploys | ✅ | rolling updates with `maxUnavailable: 0`, preStop, PodDisruptionBudgets |
| Database backups / point-in-time recovery | ⚠️ | the platform's job (managed PostgreSQL) |
| Redis high availability | ⚠️ | a single instance here; correctness never depends on it, but production would use a replicated Redis |

### Scalability and performance

| Item | Status | Notes |
| --- | :---: | --- |
| Horizontal scaling | ✅ | HPA for the API (2–8) and the gateway (2–6) |
| Data at scale | ✅ | 850,000 products and 5 million movements; indexes measured |
| Capacity | ✅ | one 8-CPU laptop: 18,177 req/s of reads, ~2,400 req/s worst-case mix, 0 errors ([results](load-test-results.md)) |
| Caching | ✅ | four layers, each measured ([flows](flows.md#1-caching-where-the-repeated-work-goes)) |
| Frontend performance | ✅ | Lighthouse: Performance 97–100, Accessibility 100, Best Practices 100 (mobile and desktop) |

### Observability

| Item | Status | Notes |
| --- | :---: | --- |
| Structured logs | ✅ | JSON logs, source-generated, trace id in every problem response |
| Metrics and traces | ❌ → see [Before go-live](#before-go-live) | no OpenTelemetry export yet |
| Alerts on SLOs | ⚠️ | needs the metrics above and the platform's alerting |

### Delivery

| Item | Status | Notes |
| --- | :---: | --- |
| CI | ✅ | build with warnings as errors, all test suites, lint, manifest validation, deploy to a real cluster, k6, the PDF check and Playwright |
| Database migrations | ✅ | a Job before the new pods; large indexes built concurrently |
| Separate deployments for frontend and backend | ✅ | one manifest and one deploy script each |
| GitOps / environments | ⚠️ | scripts deploy to the current cluster; production: Argo CD or Flux, one overlay per environment |

## Before go-live

Environment work, in order of importance:

1. **TLS** at the edge and HSTS; real domain.
2. **Secrets** from a secret manager; generate a new signing key and consumer secrets (never reuse the local ones).
3. **Managed PostgreSQL** with backups and point-in-time recovery; later read replicas for lists and searches.
4. **Metrics and traces** (OpenTelemetry → Prometheus/Grafana/Tempo or the cloud equivalent) and alerts on the
   latency and error-rate SLOs used by the load tests.
5. **Multi-node cluster** with Pod Security admission (`restricted`), a replicated Redis, and the HPAs' limits
   re-measured on the real nodes.
6. **Registry** with vulnerability scanning; GitOps deployment per environment.

## How to verify everything

```bash
scripts/deploy-all.sh                      # local cluster
scripts/check-requirements.py              # the PDF, 14 checks
cd frontend && npm run e2e                 # Playwright, desktop and mobile
cd frontend && npm run audit:lighthouse    # Lighthouse user flow (FORM_FACTOR=desktop for desktop)
load-tests/verify-data.sh                  # data invariants
```

Or ask the `quality-gate` agent (`.claude/agents/quality-gate.md`) to run all of it and report.
