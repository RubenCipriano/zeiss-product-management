import { InjectionToken } from '@angular/core';

/**
 * Where the API is: the start of every API URL (`/api`). An InjectionToken (a named value Angular can inject), so tests
 * could point it elsewhere.
 *
 * Relative by default: in development the Angular dev server proxies `/api` to the backend
 * (proxy.conf.json), and in Docker nginx does the same. The browser therefore always talks to a
 * single origin and no CORS configuration is needed.
 */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  factory: () => '/api',
});
