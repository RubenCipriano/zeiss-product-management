// The one error type the app works with (ApiError), built from whatever a failed HTTP call returned.
import { HttpErrorResponse } from '@angular/common/http';

/** The error body the API returns for every error: the standard "problem details" format (RFC 9457). */
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

  /**
   * First message for a form field (camelCase, as the API keys its validation errors). Framework-generated errors may
   * name the field by its C# property (`StockQuantity`) or JSON path (`$.stockQuantity`): those match too.
   */
  fieldError(field: string): string | null {
    const key = Object.hasOwn(this.fieldErrors, field)
      ? field
      : Object.keys(this.fieldErrors).find((key) => toFieldName(key) === field);
    return key ? (this.fieldErrors[key][0] ?? null) : null;
  }
}

/** `$.stockQuantity` / `StockQuantity` -> `stockQuantity`. */
function toFieldName(key: string): string {
  const name = key.startsWith('$.') ? key.slice(2) : key;
  return name.charAt(0).toLowerCase() + name.slice(1);
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
