import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { API_BASE_URL } from '../config/api-config';
import { ApiError } from '../http/api-error';
import { Auth } from './auth';

/**
 * Adds the access token to API calls (and only to API calls: tokens must never leak to other origins).
 * A 401 means the session is gone (expired or revoked), so the user is sent to sign in again.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const apiBaseUrl = inject(API_BASE_URL);
  const token = auth.accessToken();

  const isApiCall = req.url === apiBaseUrl || req.url.startsWith(`${apiBaseUrl}/`);
  const request =
    token && isApiCall ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

  return next(request).pipe(
    catchError((error: unknown) => {
      const status =
        error instanceof ApiError || error instanceof HttpErrorResponse ? error.status : null;
      if (isApiCall && status === 401) {
        void auth.login(window.location.pathname + window.location.search);
      }
      return throwError(() => error);
    }),
  );
};
