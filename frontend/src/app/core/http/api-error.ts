import { HttpErrorResponse } from '@angular/common/http';

/** RFC 9457 problem details, as returned by the API for every error. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errorCode?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

/**
 * The single error type the rest of the application deals with. HTTP and transport details are
 * normalised here, so components only ever look at `status`, `code`, `message` and `fieldErrors`.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly fieldErrors: Readonly<Record<string, readonly string[]>> = {},
    readonly problem: ProblemDetails | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  static fromHttpErrorResponse(response: HttpErrorResponse): ApiError {
    if (response.status === 0) {
      return new ApiError(
        0,
        'network_error',
        'The server could not be reached. Check your connection and try again.',
      );
    }

    const problem = isProblemDetails(response.error) ? response.error : null;
    return new ApiError(
      response.status,
      problem?.errorCode ?? `http_${response.status}`,
      problem?.detail ?? problem?.title ?? defaultMessage(response.status),
      problem?.errors ?? {},
      problem,
    );
  }

  /** First message for a field, matching the camelCase keys the API uses. */
  fieldError(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

function isProblemDetails(body: unknown): body is ProblemDetails {
  return typeof body === 'object' && body !== null && ('title' in body || 'detail' in body);
}

function defaultMessage(status: number): string {
  if (status >= 500) {
    return 'Something went wrong on the server. Please try again later.';
  }
  if (status === 404) {
    return 'The requested item no longer exists.';
  }
  return 'The request could not be completed.';
}
