import { DestroyRef, computed, inject, signal } from '@angular/core';

/**
 * Whole seconds counting down to zero, as a signal — what a `Retry-After` header becomes in the
 * UI: a disabled retry button with a visible number. Must be created in an injection context;
 * it stops with its owner.
 */
export class Countdown {
  readonly remaining = signal(0);
  readonly active = computed(() => this.remaining() > 0);

  private handle: ReturnType<typeof setInterval> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  start(seconds: number | null | undefined): void {
    this.stop();
    const whole = Math.ceil(seconds ?? 0);
    if (whole <= 0) {
      return;
    }
    this.remaining.set(whole);
    this.handle = setInterval(() => {
      const next = this.remaining() - 1;
      this.remaining.set(Math.max(next, 0));
      if (next <= 0) {
        this.stop();
      }
    }, 1000);
  }

  stop(): void {
    if (this.handle !== null) {
      clearInterval(this.handle);
      this.handle = null;
    }
    this.remaining.set(0);
  }
}
