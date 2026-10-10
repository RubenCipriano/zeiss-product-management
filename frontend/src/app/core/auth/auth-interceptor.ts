import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { API_BASE_URL } from '../config/api-config';
import { ApiError } from '../http/api-error';
import { Auth } from './auth';

/**
 * Adds the signed-in user's access token to every API call. An interceptor is a function that runs on every HTTP
 * request the app makes (and on its response), so no service has to add the token itself.
 *
 * Only API calls get the token: it must never leak to another site. A 401 from the API means the session is no longer
 * valid, so the user is signed out and sent to the login page.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const router = inject(Router);
  const apiBaseUrl = inject(API_BASE_URL);
  const token = auth.accessToken();

  const isApiCall = req.url === apiBaseUrl || req.url.startsWith(`${apiBaseUrl}/`);
  const isTokenRequest = auth.isTokenRequest(req.url);
  const request =
    token && isApiCall && !isTokenRequest
      ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
      : req;

  return next(request).pipe(
    catchError((error: unknown) => {
      const status =
        error instanceof ApiError || error instanceof HttpErrorResponse ? error.status : null;
      // A failed sign-in is handled by the login form itself.
      if (isApiCall && !isTokenRequest && status === 401) {
        auth.logout();
        void router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
      }
      return throwError(() => error);
    }),
  );
};
