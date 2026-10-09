# 0001 — Clean architecture with a CQRS-lite split

**Status:** accepted

## Context

The assessment asks for thin controllers, business logic in the right layers and production-grade maintainability.
The domain is small (one aggregate), so the structure should separate concerns without adding ceremony.

## Decision

Four projects with dependencies pointing inwards. Following the usual .NET convention, each project sits in its
own folder directly under `backend/` (no `src/`), and Visual Studio groups them with the `Source`, `Tests` and
`Mcp` solution folders.

```
Api ──► Application ──► Domain
 │           ▲
 └──► Infrastructure
```

| Layer              | Owns                                                                                      | Does not know about       |
| ------------------ | ----------------------------------------------------------------------------------------- | ------------------------- |
| **Domain**         | `Product`, `Category`, invariants, `ProductRules`, the stock rule                          | EF Core, HTTP, DI         |
| **Application**    | Use cases (`ProductQueryService`, `ProductCommandService`), DTOs, validation, `Result`/`Error`, persistence ports | EF Core, HTTP |
| **Infrastructure** | EF Core `DbContext`, configurations, migrations, repositories, id generator, seeding       | HTTP                      |
| **Api**            | Controllers, HTTP mapping (status codes, ETag, problem details), OpenAPI, health checks    | EF Core                   |

The application layer splits **reads** and **writes**:

* `IProductQueries`: no-tracking LINQ **projections** that go straight to `ProductResponse`. No entity is materialised
  or tracked, and only the needed columns are selected.
* `IProductRepository` + `IUnitOfWork`: return tracked aggregates for load-modify-save, plus one atomic
  `TryAdjustStockAsync` (see [0003](0003-atomic-stock-updates.md)).

Controllers only translate HTTP into a use-case call and a `Result` back into HTTP (`ApiControllerBase`).

## Consequences

* Business rules can be unit-tested without a database or web host (87 unit tests).
* Swapping the database or adding another delivery mechanism (gRPC, messaging) touches one layer.
* Some mapping/indirection that a single-project CRUD app would not need. This is acceptable for a codebase meant to grow.
* Request/response DTOs live in the Application layer and are reused by the API, so they are not duplicated.
  If the public HTTP contract ever has to diverge from the use-case contract, API-specific contracts can be added then.

## Alternatives considered

* **MediatR / full CQRS**: request/handler pipelines add indirection with little benefit for 9 endpoints, and MediatR now
  requires a commercial licence. Two focused services keep navigation obvious.
* **Generic repository (`IRepository<T>`)**: leaks `IQueryable` or ends up re-implementing EF Core. Purpose-built
  read/write ports express exactly the queries the use cases need.
