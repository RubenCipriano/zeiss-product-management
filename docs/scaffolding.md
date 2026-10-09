# How the project was created

Both applications start from the **official templates**: the .NET SDK (`dotnet new`) and the Angular CLI (`ng new`,
`ng add`, `ng generate`). Everything else was built on top of them. The git history keeps the generated output and the
changes apart:

| Commit                                                             | Content                                                                 |
| ------------------------------------------------------------------ | ----------------------------------------------------------------------- |
| `chore: scaffold backend and frontend from official templates`     | Untouched output of `dotnet new` and `ng new`                            |
| `build: configure solution structure, central packages and analyzers` | Build configuration added to the generated projects                   |
| `feat(api): ...`, `test: ...`                                      | Backend implementation and tests                                         |
| `chore(frontend): add Angular Material and angular-eslint via ng add` | Untouched output of the `ng add` schematics                            |
| `feat(web): ...`                                                   | Frontend implementation (built on `ng generate` output)                 |

`git show --stat <commit>` lists exactly what each step touched.

---

## Backend: .NET 10 SDK templates

Run from `backend/`:

```bash
# Solution, SDK pinning and local tools
dotnet new sln -n ProductManagement --format sln        # classic .sln: opens in every Visual Studio / Rider version
dotnet new globaljson --sdk-version 10.0.401 --roll-forward latestFeature
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12          # local tool: `dotnet tool restore` gives everyone the same version

# Source projects (clean architecture layers)
dotnet new classlib -n ProductManagement.Domain         -o src/ProductManagement.Domain
dotnet new classlib -n ProductManagement.Application    -o src/ProductManagement.Application
dotnet new classlib -n ProductManagement.Infrastructure -o src/ProductManagement.Infrastructure
dotnet new webapi   -n ProductManagement.Api            -o src/ProductManagement.Api --use-controllers

# Test projects (official xUnit v3 template)
dotnet new install xunit.v3.templates
dotnet new xunit3   -n ProductManagement.Domain.UnitTests      -o tests/ProductManagement.Domain.UnitTests
dotnet new xunit3   -n ProductManagement.Application.UnitTests -o tests/ProductManagement.Application.UnitTests
dotnet new xunit3   -n ProductManagement.Api.IntegrationTests  -o tests/ProductManagement.Api.IntegrationTests
dotnet new xunit3   -n ProductManagement.Api.AcceptanceTests   -o tests/ProductManagement.Api.AcceptanceTests
dotnet new classlib -n ProductManagement.Testing               -o tests/ProductManagement.Testing

# Solution folders, as shown in Visual Studio's Solution Explorer
dotnet sln add src/*/*.csproj   --solution-folder src
dotnet sln add tests/*/*.csproj --solution-folder tests

dotnet new gitignore
```

Later, once the EF Core model existed, the migration was generated with the EF Core tool rather than written by hand:

```bash
dotnet ef migrations add InitialCreate \
  --project src/ProductManagement.Infrastructure \
  --startup-project src/ProductManagement.Api \
  --output-dir Persistence/Migrations
```

### What was changed after generation

| Generated                                         | Change and reason                                                                                     |
| ------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| Per-project `TargetFramework`, `Nullable`, etc.   | Moved to `Directory.Build.props` so every project shares them. Also added warnings-as-errors, analyzers and code style enforced at build time. |
| `Version="..."` on each `PackageReference`        | Moved to `Directory.Packages.props` (Central Package Management): one place to update versions.       |
| `Class1.cs`, `UnitTest1.cs`                       | Removed (template placeholders).                                                                      |
| `WeatherForecast*.cs`                             | Removed and replaced by `ProductsController` and `CategoriesController`.                               |
| `Program.cs`                                      | Composition root: `AddApplication()`, `AddInfrastructure()`, `AddApiPresentation()`, problem details, exception handler, OpenAPI + Swagger UI, health checks, migrations at startup. |
| `appsettings*.json`                               | Connection string, database options, OpenAPI/CORS switches, JSON logs in production.                  |
| `launchSettings.json`                             | Fixed port 5080 (the frontend proxy points to it), opens Swagger on F5.                               |
| `ProductManagement.Api.http`                      | Replaced with a request for every endpoint (runs from Visual Studio, Rider or VS Code).               |
| `global.json`                                     | Kept as generated, including the `"test": { "runner": "Microsoft.Testing.Platform" }` entry that `dotnet new xunit3` added. |
| `xunit.runner.json` (integration/acceptance)      | `parallelizeTestCollections: false`: these tests share one database container.                       |
| `ProductManagement.sln`                           | Added a "Solution Items" folder (build props, `global.json`, `docker-compose.yml`, `README.md`) so they are visible in Visual Studio. |
| (new) `Dockerfile`, `.dockerignore`, `.editorconfig` | Container build and code style rules.                                                             |

---

## Frontend: Angular CLI 22

Run from the repository root (Node 24 LTS):

```bash
npx @angular/cli@22 new product-management-web --directory frontend \
  --style=scss --routing --ssr=false --skip-git --package-manager=npm --ai-config=none
```

Then, from `frontend/`:

```bash
# Official schematics: they install packages and update the configuration
ng add @angular/material      # theme in styles.scss, Roboto + Material Symbols fonts in index.html
ng add angular-eslint         # eslint.config.js and the `ng lint` target

# Building blocks (CLI conventions: Angular 22 file naming, OnPush by default, matching .spec.ts files)
ng g interceptor core/http/api-error
ng g service     core/notifications/notifications
ng g service     features/products/data-access/products-api
ng g service     features/products/data-access/categories-api
ng g service     features/products/data-access/products-store
ng g component   features/products/product-list
ng g component   features/products/product-form-dialog
ng g component   features/products/stock-adjust-dialog
ng g component   features/products/stock-status
ng g component   shared/ui/confirm-dialog
```

### What was changed after generation

| Generated                          | Change and reason                                                                                         |
| ---------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `app.ts` / `app.html` (welcome page) | Replaced with the application shell (toolbar + `<router-outlet>`).                                     |
| `app.routes.ts` (empty)            | `/products` lazy-loaded, redirects for `''` and unknown paths.                                            |
| `app.config.ts`                    | `provideHttpClient(withFetch(), withInterceptors([apiErrorInterceptor]))`, Material defaults (outlined fields, Material Symbols icons), EUR currency. |
| `angular.json`                     | `proxyConfig` for `ng serve` (`/api` → `http://localhost:5080`).                                          |
| `package.json`                     | `lint`, `test:ci`, `format`, `format:check` scripts. `engines` pins the supported Node versions.          |
| Generated `*.spec.ts` ("should create") | Replaced with behaviour-focused tests (HTTP contract, store behaviour, dialogs, list).               |
| Generated service/component stubs  | Implemented (see [ADR 0007](adr/0007-frontend-state.md)).                                                 |
| (new) `proxy.conf.json`, `Dockerfile`, `nginx.conf`, `.nvmrc`, `src/testing/` | Dev proxy, container build, Node pin, shared test fixtures.                    |

Not generated by a tool: `core/http/api-error.ts`, `core/config/api-config.ts`, `shared/forms/*`,
`shared/models/paged-response.ts`, `features/products/data-access/product-models.ts`. These are plain TypeScript
files (types and functions), and the CLI has no generator for them.
