// A small browser-side cache for API reads, so going back to a page already seen is instant. Only GET requests that
// ask for it are cached; any change sent to the API empties it. Details: the project guide, chapter 3 (caching).
import { HttpContextToken, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { concat, finalize, of, tap } from 'rxjs';
import { ResponseCache } from './response-cache';

/** How long a cached response may be used. */
export interface ClientCachePolicy {
  /** Younger than this: answered from the cache, no request at all. */
  freshMs: number;
  /** Younger than this: answered from the cache at once, then refreshed from the API (stale-while-revalidate). */
  staleMs: number;
}

/**
 * How a request opts in. An HttpContext token is a typed flag attached to one request and read by interceptors; it is
 * never sent to the server: `http.get(url, { context: new HttpContext().set(CLIENT_CACHE, policy) })`.
 */
export const CLIENT_CACHE = new HttpContextToken<ClientCachePolicy | null>(() => null);

/** Lists and histories: instant when revisited, never more than a few seconds behind once the refresh lands. */
export const LIST_CACHE_POLICY: ClientCachePolicy = { freshMs: 5_000, staleMs: 5 * 60_000 };

/**
 * The interceptor (runs on every HTTP call) that implements the cache, for GET requests that opt in with
 * {@link CLIENT_CACHE}. Going back to a page already seen is instant, and paging quickly does not send a request per
 * click for pages already loaded.
 *
 * - Fresh entry: answered from the cache, no request. Stale entry: answered from the cache at once, then the API's
 *   answer follows and replaces it ("stale-while-revalidate"). Older or missing: a normal request.
 * - A request with `Cache-Control: no-cache` (after the user's own change, or an explicit refresh) skips the cache
 *   but refreshes it.
 * - Any request that changes data (POST, PUT, DELETE) empties the cache when it completes: a change can affect any
 *   list, so nothing older is trusted.
 */
export const clientCacheInterceptor: HttpInterceptorFn = (req, next) => {
  const cache = inject(ResponseCache);

  if (req.method !== 'GET' && req.method !== 'HEAD') {
    return next(req).pipe(finalize(() => cache.clear()));
  }

  const policy = req.context.get(CLIENT_CACHE);
  if (!policy) {
    return next(req);
  }

  const key = req.urlWithParams;
  const fromApi = next(req).pipe(
    tap((event) => {
      if (event instanceof HttpResponse && event.ok) {
        cache.set(key, event);
      }
    }),
  );

  const entry = req.headers.has('Cache-Control') ? undefined : cache.get(key);
  const age = entry ? Date.now() - entry.storedAt : Infinity;
  if (entry && age < policy.freshMs) {
    return of(entry.response.clone());
  }
  if (entry && age < policy.staleMs) {
    return concat(of(entry.response.clone()), fromApi);
  }
  return fromApi;
};
