import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  DEFAULT_CURRENCY_CODE,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { Auth } from './core/auth/auth';
import { authInterceptor } from './core/auth/auth-interceptor';
import { apiErrorInterceptor } from './core/http/api-error-interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Complete a sign-in redirect / restore the session before the first navigation.
    provideAppInitializer(() => inject(Auth).initialize()),
    provideRouter(routes),
    // Order matters: the token is added first; errors are normalised to ApiError on the way back.
    provideHttpClient(withFetch(), withInterceptors([authInterceptor, apiErrorInterceptor])),
    // Single place that decides the currency shown by the `currency` pipe.
    { provide: DEFAULT_CURRENCY_CODE, useValue: 'EUR' },
  ],
};
