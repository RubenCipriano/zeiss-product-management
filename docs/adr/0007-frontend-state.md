# 0007 — Frontend state with signals and `rxResource`

**Status:** accepted

## Context

The UI must consume every endpoint, handle async data correctly (loading, errors, cancellation, races), and *"reflect
changes made through the API"*, including changes made by other users or other clients.

## Decision

* **Angular 22, standalone components, zoneless, OnPush (the v22 default), signals throughout.**
* A component-scoped **`ProductsStore`** (`@Service({ autoProvided: false })`, provided by the list page) holds the
  query (filter + page) as a signal. **`rxResource`** derives the data from it:
  * changing the query automatically cancels the in-flight request (no out-of-order responses);
  * the previous page stays visible while reloading (no flicker);
  * the query signal uses structural equality, so re-applying the same filter does not trigger a request.
* **Freshness:** after each mutation the store shows the server's response immediately, then re-fetches, because the
  change may move the product out of the current filter or page. A **background refresh every 15 s** (paused while the
  tab is hidden) shows changes made by others. A manual **Refresh** button is always available.
* **One error type:** an HTTP interceptor converts every failure to `ApiError` (status, `errorCode`, message, field
  errors), so components never parse `HttpErrorResponse`.
* **Optimistic concurrency in the UI:** edits and deletes send `If-Match`. A 412 shows a conflict banner with
  "Reload latest" rather than overwriting someone else's change.
* **Same-origin API** (`/api`): the dev server proxies to the backend, and in Docker nginx does. No CORS in the
  default setup.
* Feature-first folders: `core/` (cross-cutting), `shared/` (reusable UI and form helpers), `features/products/`
  (`data-access/` and UI).

## Consequences

* Polling is simple, works across many API instances and needs no extra infrastructure. The cost is up to 15 s of
  staleness and one small request per interval per open tab.
* **Future:** push updates with SignalR/WebSockets (needing a backplane such as Redis or Azure SignalR when scaled out),
  or Server-Sent Events, if near-real-time updates become a requirement.
