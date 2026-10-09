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
* **Summary cards** (total / low stock / out of stock) come from the same list endpoints with `pageSize=1`, using
  only `totalCount`, so no statistics endpoint is needed and the counts are cached like any other read.
* **Freshness:** after each mutation the store shows the server's response immediately, then re-fetches, because the
  change may move the product out of the current filter or page. A **background refresh every 15 s** (paused while the
  tab is hidden) shows changes made by others. A manual **Refresh** button is always available.
* **Client-side cache** (`ResponseCache` + `clientCacheInterceptor`, opt-in per request with the `CLIENT_CACHE`
  context token). Lists and histories younger than 5 s are answered from memory without a request; up to 5 minutes
  old they are shown at once and refreshed from the API (stale-while-revalidate). Revisiting a page is instant, and
  quick paging back and forth does not send a request per click. Rules that keep it correct and private:
  * memory only, never `localStorage`; cleared on sign-out, so the next user cannot see the previous one's data;
  * any write (POST, PUT, DELETE) empties it when it completes;
  * requests marked fresh (`Cache-Control: no-cache`, after the user's own changes or **Refresh**) skip it.
* **Prefetch:** once a page loads, the next one is requested in the background, so "next" is usually instant.
* **Loading feedback without jumps:** a thin progress bar (`app-progress-bar`) whenever data is loading; when the
  user changes page or filter (not on background refreshes) the current rows stay but are dimmed, after a 150 ms
  delay so fast answers never flash, and the table is `aria-busy`. Changing page again cancels the request in flight.
* **Pipes and barrels:** display rules live in pure pipes (`signedNumber`, `movementReason`) instead of template
  expressions; `shared/ui` and `shared/pipes` export through an `index.ts`. Standalone components replace NgModules
  (the Angular default since v19): each component imports exactly what it uses, which keeps lazy chunks small.
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
