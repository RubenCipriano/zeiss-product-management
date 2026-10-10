# 0005 — Result pattern and RFC 9457 problem details

**Status:** accepted

> **Update (2026-10-10):** two error types were added to `ErrorType`: `Unprocessable` → 422 (a well-formed request
> that cannot be processed, e.g. `idempotency_key_reused` found by the database) and `Unavailable` → 503 (e.g.
> `product.ids_exhausted`). Validation error keys follow the request's JSON names in camelCase (`min`, `clientId`),
> including model-binding errors and the token endpoint, which is now validated with FluentValidation too.

## Context

Error behaviour must be predictable for clients, and the code must make expected failures (validation, not found,
insufficient stock, version mismatch) easy to see and test.

## Decision

* **Expected failures are values.** Use cases return `Result<T>` / `Result` carrying an `Error`
  (`Code`, `Message`, `Type`, optional field errors and metadata). Signatures show what can go wrong, and tests assert
  on outcomes rather than on exceptions.
* **Unexpected failures are exceptions.** `GlobalExceptionHandler` (`IExceptionHandler`) logs them and returns a generic
  500 without leaking internals. Aborted client requests are logged at Debug, not as errors.
* **One wire format for every error:** `application/problem+json` (RFC 9457), produced through ASP.NET Core's
  `ProblemDetailsFactory`, so it carries the same `type`, `title`, `status`, `detail`, `instance` and `traceId` as
  framework-generated errors, plus:
  * `errorCode`: a stable, machine-readable code (`validation_failed`, `product.not_found`,
    `product.insufficient_stock`, `product.stock_limit_exceeded`, `product.version_mismatch`,
    `product.concurrent_modification`, `internal_error`; framework errors such as an unknown route get
    `http_<status>`, e.g. `http_404`);
  * `errors`: field → messages (camelCase, matching the JSON the client sent);
  * extra context where useful (`requested`, `available`, `maximum` for stock conflicts).
* Every problem is built in one way (`ApiProblems`); the codes produced at the HTTP edge (idempotency, rate limiting,
  the token endpoint, `internal_error`, `http_<status>`) are constants in `ApiErrorCodes`.
* Model-binding errors (malformed JSON, `page=abc`) are reshaped to the same format. The redundant "request field is
  required" message is removed, and serializer exception text is hidden so CLR type names don't leak.
* Unknown routes also return problem details (`UseStatusCodePages`).

| `ErrorType`          | HTTP |
| -------------------- | ---- |
| `Validation`         | 400  |
| `NotFound`           | 404  |
| `Conflict`           | 409  |
| `PreconditionFailed` | 412  |

## Validation

* **FluentValidation in the application layer** (not DataAnnotations on DTOs), so rules are unit-testable, reusable
  between create and update (`ProductWriteRequestValidator<T>`) and independent of MVC.
* Request DTO value types are **nullable** (`decimal? Price`). A missing field then fails with *"'Price' must not be
  empty"* instead of silently deserialising as `0`. This deviates from the usual non-nullable DTO style and is deliberate.
  `stockQuantity: 0` is a valid value, so "absent" and "zero" must be distinguishable.
* JSON number handling is **strict** (`"price": "12"` is rejected).
* Rules that need the database (category exists) run in the service after validation. Invariants are guarded again in
  the domain (`DomainException`) and in the database (check constraints).

## Consequences

* Clients can branch on `errorCode` without parsing messages. The Angular client maps `errors` straight onto form
  fields.
* A little more code than throwing exceptions and catching them in middleware, but control flow stays explicit and cheap.
