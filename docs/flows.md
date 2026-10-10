# Flows

Every important flow of the system, end to end, as Mermaid diagrams (rendered by GitLab and VS Code). Each
diagram was drawn from the code as it is now; the links under each one point to the files that implement it and to
the decision record (ADR) that explains why. The static picture (topology, components, data model) is in
[architecture.md](architecture.md).

| # | Flow | Diagram | Main code |
| -: | ---- | ------- | --------- |
| 1 | [Caching: where the repeated work goes](#1-caching-where-the-repeated-work-goes) | flowcharts, sequence, table | `response-cache.ts`, `default.conf.template`, `Caching/` |
| 2 | [Sign-in](#2-sign-in) | sequence | `auth.ts`, `AuthController`, `AccessTokenIssuer` |
| 3 | [Authenticated request through the gateway](#3-authenticated-request-through-the-gateway) | sequence | `jwt.js`, `default.conf.template` |
| 4 | [Read path, request by request](#4-read-path-request-by-request) | sequence | `client-cache-interceptor.ts`, `CachingProductQueries` |
| 5 | [Stock change with an Idempotency-Key](#5-stock-change-with-an-idempotency-key) | sequence | `IdempotencyFilter`, `ProductRepository` |
| 6 | [Create a product, update with If-Match](#6-create-a-product-update-with-if-match) | sequences | `ProductCommandService`, `UnitOfWork` |
| 7 | [Rate limiting](#7-rate-limiting) | flowchart | `SharedTokenBucketRateLimiter`, `RedisDistributedRateLimiter` |
| 8 | [Frontend: the product list screen](#8-frontend-the-product-list-screen) | flowchart | `products-store.ts`, `products-api.ts` |
| 9 | [Deployment and CI/CD](#9-deployment-and-cicd) | flowcharts | `infra.sh`, `deploy.sh` (backend, frontend), `.gitlab-ci.yml` (×3) |
| 10 | [Autoscaling](#10-autoscaling) | flowchart | `hpa.yaml`, `gateway.yaml`, `default.conf.template` |
| 11 | [Load test and verification](#11-load-test-and-verification) | flowcharts | `backend/load-tests/`, `check-requirements.py` |
| 12 | [Logs, from a request to Grafana](#12-logs-from-a-request-to-grafana) | flowchart | `trace.js`, `config.alloy`, `server-agent.alloy`, `browser-errors.ts` |

Names used in the diagrams: **gateway** is the nginx Deployment in front of everything (`frontend/gateway/`),
**API** is any replica of the ASP.NET Core Deployment, reached per request through the headless Service
`api`. Paths are relative to the repository root.

---

## 1. Caching: where the repeated work goes

Thousands of users mostly ask for the same few things: the first pages of the list, popular searches, the low-stock
view. Without caches, every one of those requests repeats the same work all the way down.

### Without caches

```mermaid
flowchart LR
    users(["thousands of users"]) -->|"GET page 1, again and again"| gw["nginx gateway"]
    gw -->|"every request"| api["API replica"]
    api -->|"every request:<br/>COUNT over 850,000 rows<br/>+ index walk for the page"| pg[("PostgreSQL")]
    pg -.->|"rows"| api
    api -.->|"build JSON, send"| gw
    gw -.-> users
    repeated["the repeated work:<br/>query, build JSON, send,<br/>for every single request"]:::note
    api --- repeated
    classDef note fill:#fff4d6,stroke:#d9a400,color:#333
```

### With our caches

Four layers, in the order a request meets them. A hit at any layer answers from there and the layers to the right
never see the request.

```mermaid
flowchart LR
    users(["thousands of users"]) --> b

    b["<b>1. Browser</b><br/>ResponseCache, per tab, memory only<br/>key: URL with params<br/>fresh 5 s, stale up to 5 min<br/>+ next page prefetched"]
    g["<b>2. nginx gateway</b><br/>microcache, per gateway pod<br/>key: method, URI, roles<br/>200 answers to GET, HEAD for 1 s<br/>concurrent misses wait (cache lock)"]
    a["<b>3. API + Redis</b><br/>keys versioned by generations<br/>count + page ids 10 min<br/>details 5 min, history 1 min<br/>stock-level pages 1 min<br/>single-flight per replica"]
    p[("<b>4. PostgreSQL</b><br/>source of truth")]

    b -->|"miss, or stale:<br/>shown at once, then revalidated"| g
    g -->|"miss, expired,<br/>or Cache-Control / Pragma"| a
    a -->|"miss: the query, once per replica<br/>always: 20 rows by primary key<br/>all writes"| p

    b -.->|"hit under 5 s:<br/>no request at all"| done(["answer on screen"])
    g -.->|"HIT: the API is not called"| done
    a -.->|"Redis hit: no COUNT,<br/>no index walk"| done
```

What still reaches PostgreSQL: cache misses (once per key per replica, thanks to single-flight), the read of the
page's rows by primary key for every list or search page that reaches the API (about 1 ms for 20 rows, which is why
the stock shown is always current), and every write.

### What a write invalidates

Redis entries are never deleted: every key embeds the *generations* its value depends on, and a write increments
them right after its commit ([`RedisCatalogCache.InvalidateAsync`](../backend/ProductManagement.Infrastructure/Caching/RedisCatalogCache.cs)).
Entries under an old generation are simply never read again and expire on their own.

```mermaid
flowchart LR
    stock["stock change<br/>add-to-stock, decrement-stock<br/>CatalogChange.Stock"]
    edit["create, update, delete<br/>CatalogChange.Products"]
    mig["migrate command<br/>(before each rollout)"]

    dg["data generation"]
    lg["listing generation"]
    pgen["product:{id} generation"]

    stock --> dg & pgen
    edit --> dg & lg & pgen
    mig --> dg & lg

    dg --> sl["stock-level pages"]
    lg --> lists["list and search:<br/>count + page ids"]
    lg --> det["product details,<br/>stock history"]
    pgen --> det
```

A stock change therefore leaves the full list and every search cached (which products they contain and in which
order did not change); the page still shows the new quantity because its rows are read fresh by primary key.

### 1,000 users open page 1 within the same second

```mermaid
sequenceDiagram
    autonumber
    participant U1 as User 1 browser
    participant Un as Users 2 to 1000
    participant G as gateway pod
    participant A as API replica
    participant R as Redis
    participant P as PostgreSQL

    Note over U1,Un: all stock clerks (same roles), browser caches empty
    U1->>G: GET /api/products?page=1&pageSize=20
    Un->>G: the same GET, same second
    G->>G: MISS for method, URI, roles<br/>proxy_cache_lock: one request goes on,<br/>the others wait (up to 2 s)
    G->>A: 1 request (per gateway pod)
    A->>R: MGET listing generation
    A->>R: GET count, GET page ids
    alt Redis hit
        R-->>A: count + 20 ids
    else Redis miss, first time for this generation
        A->>P: COUNT and page ids, once per replica (single-flight)
        A->>R: SET count + ids, 10 min
    end
    A->>P: the 20 rows by primary key
    A-->>G: 200
    G-->>U1: 200, X-Cache-Status MISS, entry kept 1 s
    G-->>Un: 200 from that entry, HIT
    Note over U1,Un: for 5 s each browser answers repeats itself, no request

    Note over U1,P: User 1 removes 3 units of a product shown on page 1
    U1->>G: POST /api/products/{id}/decrement-stock/3
    G->>A: POST, never cached
    A->>P: UPDATE products + INSERT stock_movements
    A->>R: INCR data generation and product:{id} generation<br/>listing generation untouched
    A-->>U1: 200, ResponseCache cleared after the write
    U1->>G: GET page 1, Cache-Control: no-cache
    G->>A: bypasses the microcache (and refreshes the entry)
    A->>R: count + ids still valid
    A->>P: 20 rows by primary key: the new stock
    A-->>U1: 200 with the new stock
    Note over Un,G: everyone else: the nginx entry is at most 1 s old,<br/>browser entries are revalidated after 5 s,<br/>the list refreshes itself every 15 s
```

With several gateway pods each one has its own microcache, so up to one request per pod (not per user) reaches the
API for a given key; with several API replicas single-flight collapses misses per replica.

### Layer by layer

| Layer | What is cached | Key | Lifetime | Invalidated by | Measured effect |
| ----- | -------------- | --- | -------- | -------------- | --------------- |
| **Browser** `ResponseCache` + `clientCacheInterceptor` | GET requests that opt in with `CLIENT_CACHE`: product lists, searches, stock-level lists (also the summary counts), stock histories; the next page is prefetched into it. Not cached: product by id, writes. | `req.urlWithParams` | fresh 5 s (answered without a request), stale up to 5 min (shown at once and revalidated); memory only, 200 entries, least recently used out | any POST/PUT/DELETE when it completes (whole cache cleared), sign-out; a request with `Cache-Control` skips the read but stores the answer | Going back to a page already seen, and "next page", need no request. |
| **nginx microcache** (`/api/`) | `200` answers to `GET`/`HEAD`, per gateway pod; writes and the token endpoint are never cached | `$request_method\|$request_uri\|$jwt_scope` (roles from the token `jwt.js` just verified) | 1 s (`proxy_cache_valid 200 1s`); concurrent misses wait for one upstream request (`proxy_cache_lock`, 2 s), an entry being refreshed is served stale (`updating`) | time only; requests with `Cache-Control` or `Pragma` bypass it (the web app sends `no-cache` after the user's own change) | 94–96 % of browse requests answered by nginx; read capacity 5,470 → 18,177 req/s on one laptop once the key became the role instead of the token. |
| **nginx static cache** | the web app's hashed bundles (`.js`, `.css`, fonts, images) | nginx default (URL) | 7 days | new release (new file names); `index.html` is never cached | The web pods serve each bundle about once per gateway pod. |
| **Redis** (`CachingProductQueries`) | list and search: total count and the page's ids | `v{listing}:products:all:count`, `…:ids:p{page}:s{size}` (search: `products:search:{TERM}:…`) | 10 min | listing generation (create, update, delete) | Single-flight removed the stampede of 882 concurrent `COUNT(*)` (~2 s each) seen under load. |
| | stock-level pages (complete) | `v{data}:products:stock:{min}:{max}:p{page}:s{size}` | 1 min | data generation (every write) | |
| | product details | `v{listing}.{product}:product:{id}` | 5 min | listing or that product's generation | |
| | a product's stock history page | `v{listing}.{product}:product:{id}:movements:p{page}:s{size}` | 1 min | listing or that product's generation | |
| **PostgreSQL** | nothing: the source of truth | | | | Still receives misses (once per key per replica), the 20-row primary-key read of every list page that reaches the API, all writes. |

All Redis keys start with `product-management:`. If Redis is unreachable the API reads from PostgreSQL directly
(fail open); a missing generation counter restarts from the clock, so a version is never reused. Numbers from
[load-test-results.md](load-test-results.md). Code:
[`response-cache.ts`](../frontend/src/app/core/http/response-cache.ts),
[`client-cache-interceptor.ts`](../frontend/src/app/core/http/client-cache-interceptor.ts),
[`default.conf.template`](../frontend/gateway/default.conf.template),
[`CachingProductQueries.cs`](../backend/ProductManagement.Infrastructure/Caching/CachingProductQueries.cs),
[`RedisCatalogCache.cs`](../backend/ProductManagement.Infrastructure/Caching/RedisCatalogCache.cs),
[`SingleFlight.cs`](../backend/ProductManagement.Infrastructure/Caching/SingleFlight.cs),
[`CacheOptions.cs`](../backend/ProductManagement.Infrastructure/Caching/CacheOptions.cs). Decisions:
[ADR 0008](adr/0008-redis-cache.md), [ADR 0012](adr/0012-gateway.md), [ADR 0020](adr/0020-edge-token-verification.md),
[ADR 0007](adr/0007-frontend-state.md).

---

## 2. Sign-in

The login page exchanges a registered consumer's client id and secret for an access token. The token endpoint is the
only `/api` location the gateway lets through without a token. The API checks the secret against a PBKDF2 hash
(an unknown client is checked against a dummy hash, so both cases take the same time), counts failures per client IP
in Redis, and signs an ES256 JWT whose header carries the key id (`kid`) the gateway later uses to pick the public
key. The browser only *reads* the claims (user name and roles, to hide actions the user cannot perform); the
signature is verified by the gateway and the API on every request.

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant L as Login page
    participant S as Auth service
    participant G as gateway
    participant C as AuthController
    participant R as Redis
    participant I as AccessTokenIssuer

    U->>L: username + password
    L->>S: login(clientId, clientSecret)
    S->>G: POST /api/auth/token, no Bearer header
    G->>G: per-IP limit_req, no token check, no cache
    G->>C: proxy to the least busy API pod
    C->>C: rate limits: global bucket per IP,<br/>20 token requests per min per IP
    C->>R: GET signin-failures:ip:{address}
    alt 10 failures within the minute
        C-->>S: 429 too_many_failed_attempts + Retry-After
    else not blocked
        C->>I: Authenticate(clientId, clientSecret)
        I->>I: find consumer, PBKDF2-SHA256 verify<br/>(dummy hash if unknown, constant-time compare)
        alt unknown client or wrong secret
            I-->>C: null
            C->>R: INCR failures, EXPIRE 1 min on the first
            C-->>S: 401 invalid_client
        else valid
            I-->>C: JWT signed ES256, header kid<br/>claims sub, jti, roles, iss, aud, nbf, exp
            C-->>S: 200 accessToken, tokenType Bearer, expiresIn
            S->>S: decode claims (no verification)<br/>session to sessionStorage<br/>timer: sign out when the token expires
            S-->>L: signed in
            L->>U: go to returnUrl (same-app paths only) or /products
        end
    end
```

A missing client id or secret is a 400 before any of this. The login page shows "Invalid username or password" for
401 and a wait message for 429. The session lives in `sessionStorage` (survives a reload, gone with the tab); signing
out also clears the browser's response cache. Code: [`login.ts`](../frontend/src/app/features/auth/login/login.ts),
[`auth.ts`](../frontend/src/app/core/auth/auth.ts),
[`AuthController.cs`](../backend/ProductManagement.Api/Controllers/AuthController.cs),
[`AccessTokenIssuer.cs`](../backend/ProductManagement.Api/Security/AccessTokenIssuer.cs),
[`SecretHasher.cs`](../backend/ProductManagement.Api/Security/SecretHasher.cs),
[`TokenSigningKey.cs`](../backend/ProductManagement.Api/Security/TokenSigningKey.cs),
[`RedisFailedAttemptThrottle.cs`](../backend/ProductManagement.Infrastructure/Security/RedisFailedAttemptThrottle.cs).
Decision: [ADR 0009](adr/0009-authentication-authorization.md).

---

## 3. Authenticated request through the gateway

Every other `/api` request is checked at the edge first: `auth_request /_jwt` runs `jwt.js` (nginx njs). A token the
pod has verified before is answered from a shared dictionary; a new one is checked with WebCrypto against the API's
public key, downloaded from the JWKS endpoint when its `kid` is unknown. The verified roles become `X-Jwt-Scope`,
which is part of the microcache key. A rejected token gets a 401 problem-details answer from nginx itself; an accepted
one is validated again by the API (defence in depth) before rate limiting and authorization.

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant N as gateway nginx
    participant J as jwt.js
    participant D as njs shared dicts
    participant S as API via Service api
    participant A as API pod (headless Service api)

    C->>N: GET /api/products, Authorization: Bearer token
    N->>N: limit_req per client IP (429 when over)
    N->>J: auth_request /_jwt
    J->>D: jwt_tokens: token seen before?
    alt seen and not expired
        D-->>J: roles, exp
    else first time on this pod
        J->>J: decode, alg must be ES256, kid required
        J->>D: jwt_keys: public key for kid?
        opt kid unknown (at most one download per 30 s)
            J->>S: GET /.well-known/jwks.json
            S-->>J: JWKS, public EC P-256 keys
            J->>D: keep x, y per kid (1 h)
        end
        J->>J: ECDSA SHA-256 signature check<br/>iss, aud, exp, nbf with 30 s skew<br/>roles sorted into a scope
        J->>D: remember token until exp (at most 10 min)
    end
    alt valid
        J-->>N: 204, X-Jwt-Scope: roles
        N->>N: microcache lookup (flow 4)
        N->>A: proxy, least_conn
        A->>A: JwtBearer validates again (ES256 only)<br/>rate limits, then authorization policy
        A-->>N: response
        N-->>C: response + X-Cache-Status
    else invalid
        J-->>N: 401, X-Jwt-Error: reason
        N-->>C: 401 application/problem+json, invalid_token<br/>WWW-Authenticate: Bearer
    end
```

The gateway holds no secret: it can verify tokens, never issue them. The JWKS is fetched over the in-cluster headless
Service `api` (`JWKS_URL` in `frontend/deploy/k8s/overlays/<environment>/gateway.yaml`); `/.well-known/jwks.json` is not routed to the API by the
gateway, so it is not exposed outside the cluster. Code: [`jwt.js`](../frontend/gateway/jwt.js),
[`default.conf.template`](../frontend/gateway/default.conf.template),
[`SecurityServiceCollectionExtensions.cs`](../backend/ProductManagement.Api/Security/SecurityServiceCollectionExtensions.cs),
[`Program.cs`](../backend/ProductManagement.Api/Program.cs) (middleware order: forwarded headers, authentication,
rate limiter, authorization). Decisions: [ADR 0020](adr/0020-edge-token-verification.md),
[ADR 0012](adr/0012-gateway.md), [ADR 0009](adr/0009-authentication-authorization.md).

---

## 4. Read path, request by request

The same layers as [section 1](#1-caching-where-the-repeated-work-goes), in the order the code runs them, for
`GET /api/products?page=1&pageSize=20`. Searches follow the same path with a `products:search:{TERM}` key; stock-level
pages and product details are cached whole (see the table above).

```mermaid
sequenceDiagram
    autonumber
    participant St as ProductsStore
    participant BC as Browser ResponseCache
    participant G as gateway
    participant A as API CachingProductQueries
    participant R as Redis
    participant P as PostgreSQL

    St->>BC: GET /api/products?page=1&pageSize=20<br/>opted in: fresh 5 s, stale 5 min
    alt cached under 5 s ago and no Cache-Control header
        BC-->>St: cached response, no request
    else
        opt cached under 5 min ago
            BC-->>St: cached response at once
        end
        BC->>G: request + Bearer (authInterceptor)
        G->>G: per-IP limit, token check (flow 3)
        alt same method, URI and roles within 1 s, no Cache-Control or Pragma
            G-->>BC: 200 from the microcache, HIT
        else miss or bypass
            G->>A: least busy pod, concurrent misses collapsed
            A->>A: JWT, per-user limit, policy products:read, validation
            A->>R: MGET listing generation
            R-->>A: version v
            A->>R: GET v:products:all:count
            opt count missing
                A->>P: COUNT, single-flight per replica
                A->>R: SET count, 10 min
            end
            A->>R: GET v:products:all:ids:p1:s20
            opt ids missing
                A->>P: page ids ORDER BY name, id, single-flight
                A->>R: SET ids, 10 min
            end
            A->>P: SELECT rows WHERE id IN ids
            A-->>G: 200 page, rows in id order
            G->>G: store 1 s
            G-->>BC: 200, MISS or BYPASS
        end
        BC->>BC: store the response
        BC-->>St: fresh response, replaces the stale one
    end
```

The Redis version is read *before* the database: if a write commits in between, the possibly older result is stored
under the previous version, which nobody reads any more. If Redis is unreachable the version is `null` and the
query goes straight to PostgreSQL. A page beyond the total count skips the ids lookup. Code:
[`products-api.ts`](../frontend/src/app/features/products/data-access/products-api.ts),
[`client-cache-interceptor.ts`](../frontend/src/app/core/http/client-cache-interceptor.ts),
[`ProductsController.cs`](../backend/ProductManagement.Api/Controllers/ProductsController.cs),
[`CachingProductQueries.cs`](../backend/ProductManagement.Infrastructure/Caching/CachingProductQueries.cs),
[`ProductQueries.cs`](../backend/ProductManagement.Infrastructure/Products/ProductQueries.cs). Decisions:
[ADR 0008](adr/0008-redis-cache.md), [ADR 0020](adr/0020-edge-token-verification.md).

---

## 5. Stock change with an Idempotency-Key

`POST /api/products/{id}/decrement-stock/{quantity}` (and `add-to-stock`), the path that must be correct under any
concurrency and any retry. The web app sends one `Idempotency-Key` per user action (`crypto.randomUUID()`: one per
dialog, one per quick +/- click), so a double submit or a retry reaching another replica is applied once. Two guards make that hold: the claim in Redis
(`SET NX`, shared by all replicas) and, as a backstop, the key stored on the ledger row under a unique index, in the
same SQL statement as the stock change.

```mermaid
sequenceDiagram
    autonumber
    participant UI as Stock dialog / ProductsStore
    participant G as gateway
    participant A as API pipeline
    participant F as IdempotencyFilter
    participant R as Redis
    participant S as ProductCommandService
    participant P as PostgreSQL

    UI->>G: POST /api/products/123456/decrement-stock/3<br/>Idempotency-Key: k
    G->>A: token checked, POST never cached
    A->>A: JwtBearer, buckets requests + writes per user,<br/>policy stock:write (403 for viewer)
    A->>F: [Idempotent] resource filter
    F->>F: key format (400 idempotency_key_invalid)<br/>scoped key = sub:k<br/>fingerprint = SHA-256 of method, path, query, body
    F->>R: SET idempotency:sub:k in-progress NX, 1 min
    alt Redis unreachable
        F-->>UI: 503 idempotency_unavailable
    else key exists
        F->>R: GET idempotency:sub:k
        alt different fingerprint
            F-->>UI: 422 idempotency_key_reused
        else still running
            F-->>UI: 409 idempotency_key_in_progress, Retry-After 1
        else completed
            F-->>UI: stored status, body, ETag<br/>Idempotent-Replayed: true
        end
    else claimed
        F->>S: DecrementStockAsync(123456, 3)
        S->>P: WITH updated AS (UPDATE products SET stock + delta<br/>WHERE id AND stock BETWEEN min AND max RETURNING)<br/>INSERT INTO stock_movements (..., request_key) SELECT FROM updated
        alt row updated, ledger row inserted
            P-->>S: stock_after
            S->>R: INCR data generation + product generation
        else request_key already in the ledger
            P-->>S: unique violation, whole statement rolled back
            Note over S: AlreadyApplied, reported as success
        else not enough stock, or no such product
            P-->>S: no row
        end
        S->>P: re-read the product (through the cache, new generation)
        S-->>F: 200 product + ETag, 409 insufficient_stock or 404
        F->>R: SET idempotency:sub:k = response (KeyLifetime)
        F-->>UI: response
        UI->>UI: patch the row, reload fresh (flow 8)
    end
```

* **No overselling, no lost updates.** Check and change are one statement; PostgreSQL row-locks the product and
  re-evaluates `WHERE stock BETWEEN min AND max` against the latest committed row. The range is computed in C#
  (`StockAdjustment.Plan`: `MinCurrentStock`, `MaxCurrentStock`), so the SQL never overflows ([ADR 0003](adr/0003-atomic-stock-updates.md)).
* **Ledger always matches stock.** The movement is inserted by the same statement (a data-modifying CTE)
  ([ADR 0016](adr/0016-stock-ledger.md)).
* **Exactly once.** A retry is replayed from Redis; if the Redis claim was lost (expired, evicted, or released after
  a cancelled request that had already committed), the unique index `ix_stock_movements_request_key` refuses the
  second ledger row and the stock change is rolled back with it ([ADR 0017](adr/0017-idempotency-keys.md)).
* Responses with status 500 or more, and exceptions, are not stored: the key is deleted so the client can retry.
  `KeyLifetime` is 24 h by default and 1 h in the Kubernetes ConfigMap; the in-progress claim lasts 1 min.
* The re-read after the change is not cancellable: once the change may have committed, the request finishes so its
  outcome can be stored and replayed.

Code: [`IdempotencyFilter.cs`](../backend/ProductManagement.Api/Idempotency/IdempotencyFilter.cs),
[`RedisIdempotencyStore.cs`](../backend/ProductManagement.Infrastructure/Idempotency/RedisIdempotencyStore.cs),
[`ProductCommandService.cs`](../backend/ProductManagement.Core/Products/ProductCommandService.cs),
[`ProductRepository.cs`](../backend/ProductManagement.Infrastructure/Products/ProductRepository.cs),
[`StockMovementConfiguration.cs`](../backend/ProductManagement.Infrastructure/Persistence/Configurations/StockMovementConfiguration.cs),
[`stock-adjust-dialog.ts`](../frontend/src/app/features/products/feature/stock-adjust-dialog/stock-adjust-dialog.ts).

---

## 6. Create a product, update with If-Match

### Create

Only managers (`products:write`). The six-digit id comes from the PostgreSQL sequence `product_id_seq`
(`100000–999999`, `NO CYCLE`), so no two replicas can ever hand out the same id. The product and its `InitialStock`
ledger row are inserted in one transaction; the ledger row carries the request's idempotency key, so a create that
was already applied is answered with the original product instead of a second one.

```mermaid
sequenceDiagram
    autonumber
    participant UI as Product form dialog
    participant A as API pipeline
    participant F as IdempotencyFilter
    participant S as ProductCommandService
    participant P as PostgreSQL
    participant R as Redis

    UI->>A: POST /api/products, Idempotency-Key (one per dialog)
    A->>A: JWT, writes bucket, policy products:write
    A->>F: claim the key in Redis (as in flow 5)
    F->>S: CreateAsync(request)
    S->>S: FluentValidation (400 with field errors)
    S->>P: category exists? (400 if not)
    S->>P: SELECT nextval('product_id_seq')
    P-->>S: 6-digit id
    S->>P: one transaction: INSERT products<br/>+ INSERT stock_movements InitialStock, request_key
    alt committed
        S->>R: INCR data, listing and product generations
        S->>P: re-read the product
        S-->>F: 201 Created, Location, ETag
    else request_key already used (unique index)
        P-->>S: DuplicateRequestException, rolled back
        S->>P: product whose InitialStock row has this request_key
        S-->>F: 201 with the original product
    end
    F->>R: store the response
    F-->>UI: 201
    UI->>UI: close dialog, reload the list fresh
```

### Update with If-Match

The response of every product read carries `ETag: "<version>"`, the row's PostgreSQL `xmin`. The form sends it back
as `If-Match`; if the product changed in the meantime the API answers 412 and the dialog offers to load the latest
version. Updates are not idempotent-keyed (a PUT is idempotent by nature, and the version check stops a stale replay).

```mermaid
sequenceDiagram
    autonumber
    participant UI as Product form dialog
    participant A as ProductsController
    participant S as ProductCommandService
    participant P as PostgreSQL
    participant R as Redis

    UI->>A: PUT /api/products/123456, If-Match with the version
    A->>A: JWT, writes bucket, products:write<br/>If-Match must be one strong tag or * (else 400)
    A->>S: UpdateAsync(id, request, expectedVersion)
    S->>S: FluentValidation (400)
    S->>P: SELECT the product (tracked)
    alt not found
        S-->>A: 404
    else version differs from If-Match
        S-->>A: 412 product.version_mismatch
    else same version
        S->>S: apply changes, Correction movement if the stock changed
        S->>P: UPDATE ... WHERE id AND xmin = loaded version<br/>+ INSERT stock_movements Correction
        alt no row matched (changed meanwhile)
            S-->>A: 412 (409 concurrent_modification without If-Match)
        else updated
            S->>R: INCR data, listing and product generations
            S->>P: re-read the product
            S-->>A: 200 product, new ETag
        end
    end
    A-->>UI: response
    opt 412
        UI->>UI: show the conflict
        UI->>A: GET /api/products/123456 (load latest)
        UI->>UI: refill the form, user re-applies the change
    end
```

Delete follows the update path (optional `If-Match`, 412/409, then 204; the history goes with the product by cascade).
Code: [`ProductsController.cs`](../backend/ProductManagement.Api/Controllers/ProductsController.cs),
[`EntityTag.cs`](../backend/ProductManagement.Api/Http/EntityTag.cs),
[`ProductCommandService.cs`](../backend/ProductManagement.Core/Products/ProductCommandService.cs),
[`UnitOfWork.cs`](../backend/ProductManagement.Infrastructure/Persistence/UnitOfWork.cs),
[`PostgresProductIdGenerator.cs`](../backend/ProductManagement.Infrastructure/Products/PostgresProductIdGenerator.cs),
[`product-form-dialog.ts`](../frontend/src/app/features/products/feature/product-form-dialog/product-form-dialog.ts).
Decisions: [ADR 0002](adr/0002-product-id-generation.md), [ADR 0004](adr/0004-optimistic-concurrency.md),
[ADR 0017](adr/0017-idempotency-keys.md).

---

## 7. Rate limiting

Two layers. The gateway limits per client IP (coarse, cheap, before any token work). The API limits per signed-in
user (or per IP for anonymous calls) with token buckets kept **in Redis**, so a user's allowance stays the same
whether the autoscaler runs 2 replicas or 8; if Redis is unreachable each replica falls back to its own in-memory
bucket. Sign-in has its own guards (flow 2).

```mermaid
flowchart TD
    req(["request"]) --> gw{"gateway limit_req<br/>per client IP<br/>200 r/s, burst 400"}
    gw -->|"over"| n429["429 from nginx"]
    gw -->|"ok"| authn["API: UseAuthentication<br/>caller = token sub, else client IP"]
    authn --> health{"path /health?"}
    health -->|"yes"| authz
    health -->|"no"| glob["global bucket<br/>requests:user:sub or requests:ip:address<br/>50 per s, burst 100, shared"]
    glob -->|"token taken"| ep{"endpoint policy?"}
    ep -->|"writes: POST, PUT, DELETE"| wr["writes bucket<br/>writes:user:sub<br/>10 per s, burst 20, shared"]
    ep -->|"authentication: POST /api/auth/token"| fixed["fixed window<br/>20 per min per IP<br/>in memory, per replica"]
    ep -->|"none"| authz["UseAuthorization, controller"]
    wr -->|"token taken"| authz
    fixed -->|"permit"| authz
    glob -.->|"empty"| a429["429 problem details rate_limited<br/>+ Retry-After"]
    wr -.->|"empty"| a429
    fixed -.->|"window used up"| a429

    subgraph sharedbucket["how a shared bucket is taken: SharedTokenBucketRateLimiter"]
        q{"Redis reachable?"} -->|"yes"| lua["Lua script in Redis, atomic:<br/>TIME, refill, take 1, HSET, PEXPIRE<br/>key ratelimit:rate:capacity:bucket"]
        q -->|"no"| local["this replica's<br/>in-memory token bucket"]
    end
```

The global bucket is taken first, then the endpoint's own policy if it has one. Anonymous callers are partitioned by
the address in `X-Forwarded-For`, which the API trusts because a NetworkPolicy lets only the gateway reach it. Code:
[`default.conf.template`](../frontend/gateway/default.conf.template),
[`RateLimitingServiceCollectionExtensions.cs`](../backend/ProductManagement.Api/RateLimiting/RateLimitingServiceCollectionExtensions.cs),
[`SharedTokenBucketRateLimiter.cs`](../backend/ProductManagement.Api/RateLimiting/SharedTokenBucketRateLimiter.cs),
[`RedisDistributedRateLimiter.cs`](../backend/ProductManagement.Infrastructure/Security/RedisDistributedRateLimiter.cs);
values as env entries in the API's Deployment (`backend/deploy/k8s/base/deployment.yaml`) and the gateway's. Decision: [ADR 0010](adr/0010-rate-limiting.md).

---

## 8. Frontend: the product list screen

Data flows one way: the page and its actions call the store, the store changes the `query` signal, and
`rxResource` fetches.
Changing the query cancels the request in flight, so an older page can never overwrite a newer one. While loading,
`linkedSignal` keeps the previous page on screen; the table is dimmed only when the user is changing page or filter
(`isChangingPage`), not during a background refresh.

```mermaid
flowchart TB
    page["ProductsPage container<br/>InventorySummary, ProductFilters,<br/>ProductTable, Paginator"]
    page -->|"filter, page"| query["query signal<br/>structural equality"]
    page -->|"user actions"| actions["ProductActions<br/>dialogs, idempotency keys, messages"]
    actions -->|"create, delete"| fresh["reload, fresh"]
    actions -->|"update, adjustStock"| apply["applyChange:<br/>patch the row with the server answer"]
    apply --> fresh
    timer["autoRefresh every 15 s<br/>tab visible, not loading"] --> bg["reload, not fresh<br/>may be served by the caches"]

    query --> res["productsResource (rxResource)<br/>cancels the request in flight"]
    fresh --> res
    bg --> res
    fresh --> sum["summaryResource<br/>3 counts with pageSize 1<br/>combineLatest"]
    bg --> sum

    res --> loaded["loaded (linkedSignal)<br/>keeps the previous page"]
    loaded --> view["page, products, totalCount"]
    res --> changing["isChangingPage:<br/>loading and loaded query differs"]
    loaded --> prefetch["effect: prefetch the next page"]

    res & sum & prefetch --> api["ProductsApi.query<br/>fresh adds Cache-Control: no-cache"]
    api --> i1["clientCacheInterceptor<br/>browser cache, flow 4"]
    i1 --> i2["authInterceptor<br/>adds Bearer to /api calls only"]
    i2 --> i3["apiErrorInterceptor<br/>errors become ApiError"]
    i3 --> net(["fetch /api via the gateway"])
    i3 -.->|"401 on the way back"| out["authInterceptor: sign out,<br/>go to /login with returnUrl"]
```

After the user's own change the reload is *fresh* (`Cache-Control: no-cache`), which skips the browser cache read and
the gateway's microcache, so the user always sees the result of what they just did. Background refreshes are not
fresh, so a crowd of open screens is answered by the microcache. An empty name filter queries the plain list. Code:
[`products-store.ts`](../frontend/src/app/features/products/data-access/products-store.ts),
[`products-api.ts`](../frontend/src/app/features/products/data-access/products-api.ts),
[`products-page.ts`](../frontend/src/app/features/products/feature/products-page/products-page.ts),
[`product-actions.ts`](../frontend/src/app/features/products/feature/product-actions.ts),
[`auto-refresh.ts`](../frontend/src/app/shared/util/auto-refresh.ts),
[`app.config.ts`](../frontend/src/app/app.config.ts) (interceptor order),
[`auth-interceptor.ts`](../frontend/src/app/core/auth/auth-interceptor.ts),
[`api-error-interceptor.ts`](../frontend/src/app/core/http/api-error-interceptor.ts). Decision:
[ADR 0007](adr/0007-frontend-state.md).

---

## 9. Deployment and CI/CD

### Deploy to the current kubectl context

Three parts, three scripts, run in this order: `infra/infra.sh up` starts the servers outside the cluster (Docker
Compose, one file per component) and applies the cluster's log collector; `backend/deploy/deploy.sh` deploys the API;
`frontend/deploy/deploy.sh` deploys the web app and the gateway. Each deploy script renders its environment's
Kustomize overlay with this release's images pinned and applies it with `kubectl apply`. Locally they also build the
images, with an immutable tag (the commit, plus a timestamp when the working tree is dirty). Docker Desktop's
Kubernetes uses the locally built images straight from the local Docker engine; any other context needs the images
from a registry (`API_IMAGE`, `WEB_IMAGE`, `GATEWAY_IMAGE`, as the pipelines pass them), and the scripts stop without
them. Once per cluster, `infra/cluster-addons.sh` installs metrics-server.

```mermaid
flowchart TD
    subgraph infra["infra/infra.sh up [database] [redis] [logs]"]
        i1["database: volume pm-postgres-data,<br/>compose up pm-postgres"] --> i2["roles service:<br/>pg_stat_statements, roles.sql<br/>pm_migrator, pm_app"]
        i3["redis: compose up pm-redis<br/>--requirepass"]
        i4{"logs: LOKI_URL set?"}
        i4 -->|"no"| i5["compose --profile storage:<br/>Loki, Grafana, server agent"]
        i4 -->|"yes"| i6["compose: server agent only,<br/>shipping to LOKI_URL"]
        i5 & i6 --> i7["kubectl apply -k logs/k8s<br/>Alloy in observability"]
    end

    subgraph backend["backend/deploy/deploy.sh"]
        b0["local-secrets.sh, first run:<br/>signing key, consumers, passwords.env"] --> b1["docker build backend/<br/>tag = short SHA + dirty timestamp<br/>(docker-desktop; else API_IMAGE)"]
        b1 --> b2["image used from<br/>the local Docker engine"]
        b2 --> b3["docker run API image migrate<br/>as pm_migrator, PGPASSWORD:<br/>migrations, seed, cache generations"]
        b3 --> b4["render overlays/local + images<br/>secretGenerator api-secrets-hash"]
        b4 --> b5["kubectl apply<br/>rollout status api"]
    end

    subgraph frontend["frontend/deploy/deploy.sh"]
        f1["docker build frontend/<br/>and frontend/gateway/<br/>(docker-desktop; else WEB_IMAGE, GATEWAY_IMAGE)"] --> f2["images used from<br/>the local Docker engine"]
        f2 --> f3{"LOGS=off?"}
        f3 -->|"no"| f4["render overlays/local + images"]
        f3 -->|"yes"| f5["render + patch<br/>COLLECTOR_UPSTREAM empty"]
        f4 & f5 --> f6["kubectl apply<br/>rollout status web, gateway"]
    end

    i2 & i3 --> b0
    b5 --> f1
    f6 --> done["http://localhost:8080"]
```

The rendered kustomization goes to `<part>/deploy/k8s/.build/<environment>/kustomization.yaml` (not committed). The
API never migrates at start-up; its database user `pm_app` could not. Code: [`infra.sh`](../infra/infra.sh),
[`database.yaml`](../infra/database.yaml), [`roles.sql`](../infra/postgres/roles.sql),
[`backend/deploy/deploy.sh`](../backend/deploy/deploy.sh), [`local-secrets.sh`](../backend/deploy/local-secrets.sh),
[`frontend/deploy/deploy.sh`](../frontend/deploy/deploy.sh), [`collector.sh`](../frontend/gateway/collector.sh),
[`DatabaseInitializer.cs`](../backend/ProductManagement.Infrastructure/Persistence/DatabaseInitializer.cs).
Decision: [ADR 0021](adr/0021-deployment-layout.md).

### CI/CD pipelines (GitLab)

One pipeline per repository. The backend and the frontend take a commit to staging automatically and to production
after a manual, protected step, once each environment is switched on (`STAGING_ENABLED`, `PRODUCTION_ENABLED`); the
infra only validates automatically, and every deployment of a server is a manual job. They run on gitlab.com; the
staging and production jobs have not yet run against real clusters.

```mermaid
flowchart TB
    subgraph be["backend/.gitlab-ci.yml"]
        direction LR
        bt["<b>test</b><br/>unit, integration,<br/>format, manifests, SAST"] --> bb["<b>build</b><br/>api image,<br/>migrations.sql"]
        bb --> bs["<b>scan</b><br/>Trivy"]
        bs --> bms["migrate:staging<br/>private runner,<br/>pm_migrator"]
        bms --> bds["deploy:staging<br/>Secret api-secrets,<br/>deploy.sh, MIGRATIONS=skip"]
        bds --> bv["verify:requirements<br/>check-requirements.py"]
        bv --> bmp["migrate:production<br/>manual, protected"]
        bmp --> bdp["deploy:production"]
    end

    subgraph fe["frontend/.gitlab-ci.yml"]
        direction LR
        ft["<b>test</b><br/>lint, unit, build,<br/>manifests, SAST"] --> fb["<b>build</b><br/>web, gateway images"]
        fb --> fs["<b>scan</b><br/>Trivy"]
        fs --> fds["deploy:staging<br/>deploy.sh"]
        fds --> fv["verify:e2e<br/>Playwright"]
        fv --> fdp["deploy:production<br/>manual, protected"]
    end

    subgraph inf["infra/.gitlab-ci.yml"]
        direction LR
        iv["<b>validate</b><br/>compose config,<br/>logs/k8s, alloy fmt"] --> isrv["servers:env, manual<br/>database, redis, logs<br/>shell runner on each server"]
        iv --> icl["cluster-logs:env, manual<br/>collector via the agent"]
    end

    be ~~~ fe ~~~ inf
```

Merge requests stop after scan (validate for infra). The deploy jobs reach the cluster through the GitLab agent for
Kubernetes; only the backend's migrate jobs need the runner inside the private network, and only the infra's server
jobs run on the servers. Code: [`backend/.gitlab-ci.yml`](../backend/.gitlab-ci.yml),
[`deploy-environment.sh`](../backend/deploy/ci/deploy-environment.sh),
[`create-secrets.sh`](../backend/deploy/ci/create-secrets.sh), [`frontend/.gitlab-ci.yml`](../frontend/.gitlab-ci.yml),
[`infra/.gitlab-ci.yml`](../infra/.gitlab-ci.yml); the manifests checks:
[`backend/deploy/ci/validate-manifests.sh`](../backend/deploy/ci/validate-manifests.sh),
[`frontend/deploy/ci/validate-manifests.sh`](../frontend/deploy/ci/validate-manifests.sh). Setup: [guide chapter 12](guide/12-from-zero.md).

---

## 10. Autoscaling

Both the API and the gateway have a HorizontalPodAutoscaler fed by metrics-server. A new API pod gets traffic as
soon as it is ready: the gateway balances over the headless Service `api`, whose DNS lists every ready pod,
and re-resolves it every 5 s. A new gateway pod joins the `gateway` LoadBalancer Service.

```mermaid
flowchart LR
    load["more requests"] --> cpu["pod CPU rises"]
    cpu --> ms["metrics-server<br/>kube-system"]
    ms --> hapi{"HPA api<br/>average CPU over 60 %<br/>of the 200m request?"}
    ms --> hgw{"HPA gateway<br/>average CPU over 70 %<br/>of the 250m request?"}

    hapi -->|"yes"| upapi["add up to 2 pods per 30 s<br/>max 8"]
    hapi -->|"below for 3 min"| downapi["remove 1 pod per min<br/>min 2"]
    upapi --> ready["new API pod passes /health/ready<br/>PostgreSQL + Redis reachable"]
    ready --> dns["Service api DNS lists its IP"]
    dns --> lb["each gateway pod re-resolves within 5 s<br/>least_conn sends it requests"]

    hgw -->|"yes"| upgw["add gateway pods<br/>max 6, default scale-up"]
    hgw -->|"below for 3 min"| downgw["remove pods, min 2"]
    upgw --> svc["gateway Service adds the pod<br/>own microcache, token and key caches, empty at start"]
```

* **The database is the ceiling.** One PostgreSQL server; each API replica has at most 10 pooled connections plus
  one for its readiness check, so 8 replicas give PostgreSQL at most 88 (of `max_connections = 200`). Beyond that, capacity comes from the database (read replicas, a bigger instance, a
  pooler).
* Rate limits per user are shared in Redis (flow 7), so they do not grow with the number of replicas.
* Change a range without touching the other project:
  `kubectl -n product-management patch hpa api -p '{"spec":{"minReplicas":4}}'`.

Code: [`hpa.yaml`](../backend/deploy/k8s/base/hpa.yaml) (HPA `api`), [`service.yaml`](../backend/deploy/k8s/base/service.yaml)
(Service `api`), [`gateway.yaml`](../frontend/deploy/k8s/base/gateway.yaml) (HPA `gateway`),
[`default.conf.template`](../frontend/gateway/default.conf.template) (`resolver valid=5s`, `resolve`, `least_conn`),
[`cluster-addons.sh`](../infra/cluster-addons.sh). Decision:
[ADR 0018](adr/0018-autoscaling-and-scale-testing.md).

---

## 11. Load test and verification

### The teams run: concurrency, retries and the ledger

`seed-bulk.sh` loads the large catalogue; `run.sh teams` runs k6 *inside* the cluster (a Job hitting the gateway
Service) with many virtual users, signed in as the demo accounts, browsing, searching and changing stock, a share of
the changes sent twice with the same `Idempotency-Key`. Afterwards the script compares the number of changes k6 saw applied with the rows the ledger
gained: one applied change must mean exactly one ledger row. Then `verify-data.sh` checks the data invariants.

```mermaid
flowchart TD
    seed["backend/load-tests/seed-bulk.sh<br/>PRODUCTS=850000 by default"] --> s1["psql data/bulk-seed.sql<br/>products + about 5 million movements"]
    s1 --> s2["redis-cli INCR catalog:generation"]
    s2 --> s3["verify-data.sh"]
    s3 --> r1

    subgraph run["backend/load-tests/run.sh teams"]
        r1["raise the gateway per-IP limit<br/>and the API's per-user limits"] --> r2["SQL: id range,<br/>highest ledger id before"]
        r2 --> r3["ConfigMaps k6-scripts, k6-lib<br/>Job k6-teams, grafana/k6"]
        r3 --> k6
        k6 --> r4["Job complete?<br/>a crossed k6 threshold marks the run failed"]
        r4 --> r5{"ledger rows added between applied<br/>and applied + unknown?"}
        r5 -->|"no"| mm["MISMATCH: applied twice or lost"]
        r5 -->|"yes"| r6["verify-data.sh"]
        mm --> r6
        r6 --> r7["restore the saved limits on exit<br/>PASSED or FAILED"]
    end

    subgraph k6["teams.js, in the cluster"]
        t1["setup: tokens for viewer, clerk, manager<br/>50 hot products from stock-level"] --> t2["mix: 30 % list, 25 % get, 15 % search,<br/>5 % stock-level, 5 % history, 20 % stock change"]
        t2 --> t3["stock change, new Idempotency-Key<br/>10 % double click, 30 % retry after the answer<br/>same key again while the outcome is unknown"]
        t3 --> t4["handleSummary: TEAMS_RESULT applied, unknown"]
    end

    s4["data/verify.sql: 6 counts must be 0<br/>duplicate ids, ids out of range,<br/>stock vs ledger sum, products without ledger,<br/>broken running totals, negative stock"]
    r6 -.-> s4
    s3 -.-> s4
```

Other scenarios (`smoke`, `load`, `stress`, `spike`, `stock-consistency`, `rate-limit`, `browse`) use the same
`run.sh` Job without the ledger comparison. Every scenario except `rate-limit` raises the API's per-user limits for
the run, and `stress`, `spike`, `browse` and `teams` also the gateway's per-IP limit; the deployed values are saved
first and set back on exit, so no override is left behind. Code:
[`seed-bulk.sh`](../backend/load-tests/seed-bulk.sh), [`run.sh`](../backend/load-tests/run.sh), [`teams.js`](../backend/load-tests/teams.js),
[`verify-data.sh`](../backend/load-tests/verify-data.sh), [`verify.sql`](../backend/load-tests/data/verify.sql). Results:
[load-test-results.md](load-test-results.md). Decisions: [ADR 0015](adr/0015-load-testing.md),
[ADR 0018](adr/0018-autoscaling-and-scale-testing.md).

### The assessment, checked against the running system

`backend/scripts/check-requirements.py` walks the assessment PDF requirement by requirement through the gateway (so the
requests spread over the replicas), with `Cache-Control: no-cache` on every call and retrying on 429.

```mermaid
flowchart LR
    c0["check-requirements.py<br/>BASE_URL localhost:8080"] --> c1["POST /api/auth/token<br/>as manager"]
    c1 --> c2["CRUD: create 201 + 6-digit id,<br/>list, get, update,<br/>delete then 404"]
    c2 --> c3["stock: +25, -7,<br/>-1000 gives 409, unchanged"]
    c3 --> c4["search partial match,<br/>stock-level range"]
    c4 --> c5["40 concurrent creates:<br/>40 unique 6-digit ids"]
    c5 --> c6["validation 400 on create<br/>and update, stock in<br/>every list response"]
    c6 --> c7["delete its products<br/>print passed of 14<br/>exit 1 on any failure"]
```

Code: [`check-requirements.py`](../backend/scripts/check-requirements.py).

---

## 12. Logs, from a request to Grafana

The system is observed through its logs alone ([guide chapter 8](guide/08-observability.md)). The gateway keeps a
valid incoming `traceparent` or builds one from its request id, logs the `trace_id`, and passes the header on; the
API's log lines carry the same id. Both write JSON to stdout; collectors push everything to Loki on the log server,
outside the cluster (`LOKI_URL`), which keeps it for 14 days. The whole chain is optional (the infra's `logs`
component): without it the pods still log to stdout and the gateway answers `/collect` with 204. The browser has no stdout anyone can read, so the web app
reports its errors (errors only) with the Grafana Faro SDK to the gateway's `/collect`, which forwards them to Alloy's
`faro.receiver`.

```mermaid
flowchart LR
    br(["browser<br/>errors (Faro SDK)"]) -->|"POST /collect"| gw
    req(["request"]) --> gw["gateway<br/>traceparent, access log<br/>with trace_id"]
    gw -->|"traceparent"| api["API<br/>TraceId in every line"]

    gw & api -. stdout .-> alloy["Alloy (cluster)<br/>trace_id, level<br/>+ faro.receiver"]
    gw -->|"proxy /collect"| alloy
    pg[("PostgreSQL")] & rd[("Redis")] -. logs .-> agent["server agent"]

    alloy -->|"LOKI_URL"| loki["Loki"]
    agent --> loki
    loki --> graf["Grafana<br/>2 dashboards, 12 alerts,<br/>logs of one trace_id"]
```

Code: [`browser-errors.ts`](../frontend/src/app/core/observability/browser-errors.ts), [`trace.js`](../frontend/gateway/trace.js), [`LoggingExtensions.cs`](../backend/ProductManagement.Api/Observability/LoggingExtensions.cs),
[`config.alloy`](../infra/logs/k8s/config.alloy), [`server-agent.alloy`](../infra/logs/server-agent.alloy),
[`rules.yaml`](../infra/logs/grafana/provisioning/alerting/rules.yaml).
