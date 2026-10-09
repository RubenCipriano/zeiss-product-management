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

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Input binding: the login page receives ?returnUrl=... as an input.
    provideRouter(routes, withComponentInputBinding()),
    // Order matters: cached answers short-circuit first; then the token is added; errors are normalised to ApiError
    // on the way back.
    provideHttpClient(
      withFetch(),
      withInterceptors([clientCacheInterceptor, authInterceptor, apiErrorInterceptor]),
    ),
    // Single place that decides the currency shown by the `currency` pipe.
    { provide: DEFAULT_CURRENCY_CODE, useValue: 'EUR' },
  ],
};
