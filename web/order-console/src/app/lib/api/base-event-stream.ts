import { DestroyRef, inject, signal } from '@angular/core';
import { APP_CONFIG } from '../../core/config/app.config';
import { ApiError } from './api-error';

/**
 * `EventSource` as signals. Nothing else in the app may construct an `EventSource`.
 *
 * Reconnects after a dropped connection are the browser's own; a `done` event closes for good;
 * a non-200 on connect (the API's `503 sse.full`) makes the browser give up, which surfaces
 * here as `error() = ApiError(sse.full)` so the owner can fall back to `BaseResource.reload()`
 * every `APP_CONFIG.fallbackPollMs`. Every frame carries full state, never a delta.
 */
export abstract class BaseEventStream<T> {
  readonly data = signal<T | null>(null);
  readonly connected = signal(false);
  readonly error = signal<ApiError | null>(null);
  /** The server sent `done`: the aggregate is terminal and nothing can change after this. */
  readonly done = signal(false);

  private readonly base = inject(APP_CONFIG).apiBaseUrl;
  private source: EventSource | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
  }

  /** Opens `base + path` and parses the `data` of every `event` frame as `T`. */
  protected open(path: string, event: string): void {
    this.close();
    this.error.set(null);
    this.done.set(false);

    const source = new EventSource(`${this.base}${path}`);
    this.source = source;

    source.addEventListener('open', () => {
      this.connected.set(true);
      this.error.set(null);
    });

    source.addEventListener(event, (frame: MessageEvent<string>) => {
      this.data.set(JSON.parse(frame.data) as T);
    });

    source.addEventListener('done', () => {
      this.done.set(true);
      this.close();
    });

    source.addEventListener('error', () => {
      this.connected.set(false);
      // CONNECTING = the browser is retrying on its own. CLOSED = it refused (non-200 status or
      // wrong content type) and will not retry — that is how a 503 on connect reaches the client.
      if (source.readyState === EventSource.CLOSED) {
        this.error.set(ApiError.sseFull());
        this.close();
      }
    });
  }

  close(): void {
    this.source?.close();
    this.source = null;
    this.connected.set(false);
  }
}
