import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { ApiError } from './api-error';

/**
 * Interceptor (runs on every HTTP call) that turns any failure into an {@link ApiError}, so the rest of the app never
 * has to understand Angular's `HttpErrorResponse` or the API's problem-details format.
 */
export const apiErrorInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((error: unknown) =>
      throwError(() =>
        error instanceof HttpErrorResponse ? ApiError.fromHttpErrorResponse(error) : error,
      ),
    ),
  );
