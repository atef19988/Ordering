import { InjectionToken, Provider } from '@angular/core';

/** Environment the console runs against. Nothing else in the app reads `window.location`. */
export interface AppConfig {
  /** Prefix for every API call and event stream, e.g. `/api` (proxied by `ng serve`). */
  readonly apiBaseUrl: string;
  /** How often `BaseResource.reload()` is called when an event stream cannot be opened. */
  readonly fallbackPollMs: number;
}

export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');

const DEFAULT_CONFIG: AppConfig = {
  apiBaseUrl: '/api',
  fallbackPollMs: 10_000,
};

export function provideAppConfig(overrides: Partial<AppConfig> = {}): Provider {
  return { provide: APP_CONFIG, useValue: { ...DEFAULT_CONFIG, ...overrides } };
}
