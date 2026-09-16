import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../core/config/app.config';

/**
 * The single place that touches `HttpClient` (enforced by `no-restricted-imports` in
 * `eslint.config.js`). Feature services extend it and only declare routes. The response body
 * is the DTO itself — the API sends no envelope — and every failure arrives as an `ApiError`
 * because `apiInterceptor` has already mapped it.
 */
export abstract class BaseApiService {
  protected readonly http = inject(HttpClient);
  protected readonly base = inject(APP_CONFIG).apiBaseUrl;

  protected get<T>(path: string): Observable<T> {
    return this.http.get<T>(this.url(path));
  }

  protected post<T>(
    path: string,
    body?: unknown,
    headers?: Record<string, string>,
  ): Observable<T> {
    return this.http.post<T>(this.url(path), body ?? null, { headers });
  }

  private url(path: string): string {
    return `${this.base}${path.startsWith('/') ? path : `/${path}`}`;
  }
}
