/**
 * The only error a component ever sees. Built once, in `apiInterceptor`, from a ProblemDetails
 * body (`code` + extensions) and the `Retry-After` header; everything else in the app treats
 * it as a value.
 */
export class ApiError extends Error {
  constructor(
    /** Stable machine code, e.g. `stock.insufficient`; `network.unreachable` when no response came. */
    readonly code: string,
    message: string,
    /** HTTP status; `0` when the request never got a response. */
    readonly status: number,
    /** Every ProblemDetails member that is not a standard one, e.g. `{ productCode, available }`. */
    readonly extensions: Readonly<Record<string, unknown>> = {},
    /** Parsed `Retry-After` header, when the server sent one. */
    readonly retryAfterSeconds: number | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /**
   * True when the request may not have reached the database, or was turned away before it
   * did, so re-sending it with the same Idempotency-Key is safe and is the right move:
   * network errors, timeouts, every 503 (`stock.busy`, `server.busy`, `server.timeout`) and
   * `409 idempotency.in_progress`. Task 10's retry button keys off this one flag.
   */
  get isRetryableWithSameKey(): boolean {
    return (
      this.status === 0 ||
      this.status === 503 ||
      this.code === 'network.timeout' ||
      (this.status === 409 && this.code === 'idempotency.in_progress')
    );
  }

  /** Typed access to one extension, e.g. `error.extension<number>('available')`. */
  extension<T>(key: string): T | undefined {
    return this.extensions[key] as T | undefined;
  }

  /** Normalises anything thrown on an API path into an `ApiError`. */
  static from(error: unknown): ApiError {
    if (error instanceof ApiError) {
      return error;
    }
    const message = error instanceof Error ? error.message : 'Something went wrong.';
    return new ApiError('client.unexpected', message, 0);
  }

  /** The server refused an event stream on connect (`503 sse.full`); the caller falls back to polling. */
  static sseFull(): ApiError {
    return new ApiError(
      'sse.full',
      'Live updates are full right now. Showing periodic refreshes instead.',
      503,
    );
  }
}
