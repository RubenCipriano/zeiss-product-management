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

/** Opt-in per request: `http.get(url, { context: new HttpContext().set(CLIENT_CACHE, policy) })`. */
export const CLIENT_CACHE = new HttpContextToken<ClientCachePolicy | null>(() => null);

/** Lists and histories: instant when revisited, never more than a few seconds behind once the refresh lands. */
export const LIST_CACHE_POLICY: ClientCachePolicy = { freshMs: 5_000, staleMs: 5 * 60_000 };

/**
 * Client-side cache for GET requests that opt in with {@link CLIENT_CACHE}. Going back to a page already seen is
 * instant, and paging quickly does not send a request per click for pages already loaded.
 *
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
