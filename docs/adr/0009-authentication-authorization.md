# 0009 — Authentication with an OIDC provider, permission-based RBAC

**Status:** accepted

## Context

A production API must know who calls it and what they may do. Writing password storage, login pages, MFA and
token issuance by hand is high risk and duplicates what identity providers already do well.

## Decision

* **Keycloak** (OIDC) is the identity provider, deployed in the cluster for local use. In a company it would be
  the corporate IdP (Entra ID, Okta, ...); the API only depends on standard OIDC/JWT.
* **The API validates JWT access tokens** (`AddJwtBearer`): signature (keys downloaded from the IdP's JWKS), issuer,
  audience (`product-management-api`), lifetime (30 s clock skew). It never sees passwords. Claims are kept as
  issued (`MapInboundClaims = false`), so `roles`, `sub` and `preferred_username` mean what the token says.
* **Permissions, not roles, on endpoints.** Policies are named after permissions and mapped from roles in one place
  (`Policies.Configure`):

  | Policy           | Roles                          | Endpoints                                  |
  | ---------------- | ------------------------------ | ------------------------------------------ |
  | `products:read`  | viewer, stock-clerk, manager   | every GET                                  |
  | `stock:write`    | stock-clerk, manager           | decrement-stock, add-to-stock              |
  | `products:write` | manager                        | POST, PUT, DELETE                          |

* **Secure by default:** a fallback policy requires an authenticated user, so a new endpoint without attributes is
  protected. Health probes and the OpenAPI document opt out explicitly (`AllowAnonymous`).
* **Fail fast:** the API refuses to start without `Authentication:Authority`.
* **Frontend:** Authorization Code + PKCE through `oidc-client-ts` (a certified OIDC client), with silent token renewal.
  The token is only attached to `/api` calls. The UI hides actions the user's role cannot perform, but that is
  purely UX: the API enforces every rule.
* **Machine identity for the MCP server:** client-credentials grant with its own service account holding only
  `stock-clerk` (least privilege): an AI assistant cannot create, edit or delete products.
* One public issuer URL (`http://localhost:8080/auth/...`) for browsers and tokens, while the API fetches signing
  keys from the in-cluster address (`MetadataAddress` + `KC_HOSTNAME_BACKCHANNEL_DYNAMIC`).

## Consequences

* Tests do not need Keycloak: they sign tokens with a test RSA key that the test host trusts. The real validation
  pipeline runs, and a 30-case role × operation matrix, plus expired, foreign-audience and forged tokens, is covered.
* The local Keycloak runs in development mode with demo users, and the realm (clients, roles, users) is imported
  from the manifest. Production would run `kc.sh start` with its own database, TLS and no demo users.
* Coarse roles cover this domain. If row-level rules appear (e.g. stock per warehouse), resource-based
  authorization (`IAuthorizationHandler` with the resource) slots into the same policies.
