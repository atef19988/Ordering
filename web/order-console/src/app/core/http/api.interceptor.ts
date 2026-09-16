import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { TimeoutError, catchError, throwError } from 'rxjs';
import { ApiError } from '../../lib/api/api-error';
import { APP_CONFIG } from '../config/app.config';

/** ProblemDetails members that are not application data. Everything else becomes `extensions`. */
const STANDARD_MEMBERS = new Set(['type', 'title', 'status', 'detail', 'instance', 'code']);

/**
 * Applies to every request under `apiBaseUrl`: stamps a correlation id and turns any failure
 * into an `ApiError` exactly once. No component ever sees an `HttpErrorResponse`.
 */
export const apiInterceptor: HttpInterceptorFn = (request, next) => {
  const { apiBaseUrl } = inject(APP_CONFIG);
  if (!request.url.startsWith(apiBaseUrl)) {
    return next(request);
  }

  const stamped = request.clone({
    setHeaders: {
      'X-Correlation-Id': crypto.randomUUID(),
      Accept: 'application/json, application/problem+json',
    },
  });

  return next(stamped).pipe(catchError((error: unknown) => throwError(() => toApiError(error))));
};

function toApiError(error: unknown): ApiError {
  if (error instanceof TimeoutError) {
    return new ApiError('network.timeout', 'The service did not answer in time.', 0);
  }
  if (!(error instanceof HttpErrorResponse)) {
    return ApiError.from(error);
  }
  if (error.status === 0) {
    return new ApiError('network.unreachable', 'The service could not be reached.', 0);
  }

  const problem = isRecord(error.error) ? error.error : {};
  const code = typeof problem['code'] === 'string' ? problem['code'] : `http.${error.status}`;
  const message =
    (typeof problem['detail'] === 'string' && problem['detail']) ||
    (typeof problem['title'] === 'string' && problem['title']) ||
    error.message;
  const extensions = Object.fromEntries(
    Object.entries(problem).filter(([key]) => !STANDARD_MEMBERS.has(key)),
  );

  return new ApiError(
    code,
    message,
    error.status,
    extensions,
    retryAfterSeconds(error.headers.get('Retry-After')),
  );
}

/** `Retry-After` is either delta-seconds or an HTTP date; both become whole seconds from now. */
function retryAfterSeconds(header: string | null): number | null {
  if (header === null) {
    return null;
  }
  const seconds = Number(header);
  if (Number.isFinite(seconds)) {
    return Math.max(0, Math.ceil(seconds));
  }
  const at = Date.parse(header);
  return Number.isNaN(at) ? null : Math.max(0, Math.ceil((at - Date.now()) / 1000));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
