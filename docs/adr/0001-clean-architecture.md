# 0001 — Clean architecture with a CQRS-lite split

**Status:** accepted (amended: Domain and Application merged into Core, see the update below)

> **Update (2026-10-10):** the Domain and Application projects were merged into one project,
> **`ProductManagement.Core`**, organised by feature instead of by layer: `Core/Products` holds `Product`,
> `ProductRules`, `StockAdjustment`, `StockMovement`, the command and query services, `ProductErrors`, their interfaces
> and the `Abstractions/` (ports), `Contracts/` and `Validation/` folders; `Core/Categories` the same for categories;
> `Core/Common` the shared pieces (`Errors`, `Paging`, `Persistence`, `Requests`, `Results`, `Security`, `Validation`,
> `DomainException`, `Guard`). `AddCore()` registers it. The dependencies are now
> `Api ──► Infrastructure ──► Core` and `Api ──► Core`.
>
> **Why:** the Domain project had only 7 files, and the line between it and Application was never one the code relied
> on: both are free of EF Core and HTTP, and both change together when a feature changes. The boundary that matters,
> **EF Core and the database only in Infrastructure, ASP.NET Core only in Api**, is still enforced by the compiler:
> `ProductManagement.Core` references neither, so code that uses them does not build. Organising by feature puts
> everything about products in one folder. The unit tests followed: one `ProductManagement.UnitTests` project with
> the folders `Core/`, `Infrastructure/` and `Api/` (212 tests); the solution went from 13 projects to 7 (the MCP
> server and its tests were removed at the same time, [0014](0014-mcp-server.md)). The rest of this record (the
> CQRS-lite split, the ports, the Edge interfaces) is unchanged; below, "Domain" and "Application" now mean folders
> of `Core`, not projects. The text below is the original decision.

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
| **Domain**         | `Product`, `Category`, `StockMovement`, invariants, `ProductRules`, the stock rule (`StockAdjustment`) | EF Core, HTTP, DI |
| **Application**    | Use cases (`ProductQueryService`, `ProductCommandService`, `StockCommandService`), DTOs, validation, `Result`/`Error`, persistence ports | EF Core, HTTP |
| **Infrastructure** | EF Core `DbContext`, configurations, migrations, repositories, id generator, seeding, cache decorators, Redis; HTTP-edge ports and stores (`Edge/`) | HTTP |
| **Api**            | Controllers, HTTP mapping (status codes, ETag, problem details), OpenAPI, health checks    | EF Core                   |

The application layer splits **reads** and **writes**:

* `IProductQueries`: no-tracking LINQ **projections** that go straight to `ProductResponse`. No entity is materialised
  or tracked, and only the needed columns are selected.
* `IProductRepository` + `IUnitOfWork`: return tracked aggregates for load-modify-save, plus one atomic
  `TryAdjustStockAsync` (see [0003](0003-atomic-stock-updates.md)).

Controllers only translate HTTP into a use-case call and a `Result` back into HTTP (`ApiControllerBase`).

Ports that only the HTTP edge uses (the idempotency store, the shared rate limiter, the failed sign-in throttle) are
not application ports: no use case needs them, and the idempotency store holds HTTP responses. They are public
interfaces in `ProductManagement.Infrastructure.Edge`, used by the Api directly (Api → Infrastructure, as above).

## Consequences

* Business rules can be unit-tested without a database or web host (187 unit tests for Domain, Application,
  Infrastructure and Api).
* Swapping the database or adding another delivery mechanism (gRPC, messaging) touches one layer.
* Some mapping/indirection that a single-project CRUD app would not need. This is acceptable for a codebase meant to grow.
* Request/response DTOs live in the Application layer and are reused by the API, so they are not duplicated.
  If the public HTTP contract ever has to diverge from the use-case contract, API-specific contracts can be added then.

## Alternatives considered

* **MediatR / full CQRS**: request/handler pipelines add indirection with little benefit for 9 endpoints, and MediatR now
  requires a commercial licence. A few focused services keep navigation obvious.
* **Generic repository (`IRepository<T>`)**: leaks `IQueryable` or ends up re-implementing EF Core. Purpose-built
  read/write ports express exactly the queries the use cases need.
