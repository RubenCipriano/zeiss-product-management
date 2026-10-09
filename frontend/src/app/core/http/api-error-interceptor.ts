import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { ApiError } from './api-error';

/**
 * Converts every failed HTTP call into an {@link ApiError}, so callers never have to understand
 * `HttpErrorResponse` or the problem-details format.
 */
export const apiErrorInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((error: unknown) =>
      throwError(() =>
        error instanceof HttpErrorResponse ? ApiError.fromHttpErrorResponse(error) : error,
      ),
    ),
  );
