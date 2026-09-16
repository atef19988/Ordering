import { computed, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { ApiError } from '../api/api-error';

/**
 * One remote value as signals: `data`, `loading`, `error`, plus `reload()`.
 *
 * `reload()` while a load is in flight cancels the older request, so a stale response can never
 * overwrite a newer one. Owners that also hold an event stream call `reload()` on a poll timer
 * only when the stream could not be opened — never while it is connected.
 */
export class BaseResource<T> {
  readonly data = signal<T | null>(null);
  readonly loading = signal(false);
  readonly error = signal<ApiError | null>(null);
  /** True until the first load has settled, so a shell can show a spinner instead of an empty state. */
  readonly initial = computed(() => this.data() === null && this.error() === null);

  private inFlight: Subscription | null = null;

  constructor(private readonly load: () => Observable<T>) {}

  reload(): void {
    this.inFlight?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);

    this.inFlight = this.load().subscribe({
      next: (value) => {
        this.data.set(value);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(ApiError.from(error));
        this.loading.set(false);
      },
    });
  }

  /** Replaces the value without a round trip, e.g. from an event-stream frame or a command's response. */
  set(value: T): void {
    this.inFlight?.unsubscribe();
    this.data.set(value);
    this.error.set(null);
    this.loading.set(false);
  }

  clear(): void {
    this.inFlight?.unsubscribe();
    this.data.set(null);
    this.error.set(null);
    this.loading.set(false);
  }
}
