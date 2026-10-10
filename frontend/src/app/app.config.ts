import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  DEFAULT_CURRENCY_CODE,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth-interceptor';
import { apiErrorInterceptor } from './core/http/api-error-interceptor';
import { clientCacheInterceptor } from './core/http/client-cache-interceptor';
import { provideBrowserErrorReporting } from './core/observability/reporting-error-handler';

/**
 * App-wide setup, read once at startup (main.ts): the router, the HTTP client and the code that runs on every HTTP
 * call, error reporting, and the currency. A "provider" tells Angular how to create something that components and
 * services can then ask for with `inject()`.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    // Uncaught errors and unhandled promise rejections go to the ErrorHandler, which reports them to Loki (production
    // only; see core/observability).
    provideBrowserGlobalErrorListeners(),
    provideBrowserErrorReporting(),
    // Input binding: the login page receives ?returnUrl=... as an input.
    provideRouter(routes, withComponentInputBinding()),
    // Interceptors: functions that run on every HTTP request and its response. Order matters: a cached answer
    // short-circuits first; then the access token is added; on the way back every error becomes an ApiError.
    provideHttpClient(
      withFetch(),
      withInterceptors([clientCacheInterceptor, authInterceptor, apiErrorInterceptor]),
    ),
    // Single place that decides the currency shown by the `currency` pipe.
    { provide: DEFAULT_CURRENCY_CODE, useValue: 'EUR' },
  ],
};
