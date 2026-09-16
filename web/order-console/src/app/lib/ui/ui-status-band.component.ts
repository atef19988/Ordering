import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { statusView } from './status';

/**
 * The one bold element on the screen: a full-width band in the order's state colour with the
 * state's word, and the notification outcome beside it. Reads the one status map in
 * `status.ts`. The band cross-fades when the status changes — the only motion in the console —
 * and stays still under `prefers-reduced-motion`.
 */
@Component({
  selector: 'ui-status-band',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (key of [bandKey()]; track key) {
      <div class="band tone-{{ view().tone }}" role="status" aria-live="polite">
        <span class="state">{{ view().label }}</span>
        @if (notificationText(); as text) {
          <span class="note">{{ text }}</span>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
    }

    .band {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--s-1) var(--s-6);
      padding: var(--s-3) var(--s-4);
      border-radius: var(--r-md);
      color: var(--c-surface);
      animation: fade-in 200ms ease-out;
    }

    .state {
      font-size: var(--t-lg);
      font-weight: 600;
    }

    .note {
      font-size: var(--t-sm);
      opacity: 0.92;
    }

    .tone-ok {
      background: var(--c-ok);
    }

    .tone-wait {
      background: var(--c-wait);
    }

    .tone-stop {
      background: var(--c-stop);
    }

    .tone-void {
      background: var(--c-void);
    }

    @keyframes fade-in {
      from {
        opacity: 0.35;
      }

      to {
        opacity: 1;
      }
    }
  `,
})
export class UiStatusBandComponent {
  /** The order's status: `Confirmed` or `Cancelled` (any known status renders). */
  readonly status = input.required<string>();
  /** `OrderDetailDto.notificationStatus`; omit to show the order state alone. */
  readonly notification = input<string | null>(null);
  readonly attempts = input(0);

  protected readonly view = computed(() => statusView(this.status()));

  /** Changing either state re-creates the band, which is what plays the cross-fade. */
  protected readonly bandKey = computed(() => `${this.status()}|${this.notification() ?? ''}`);

  protected readonly notificationText = computed(() => {
    const notification = this.notification();
    if (notification === null) {
      return null;
    }
    const label = statusView(notification).label;
    const attempts = this.attempts();
    switch (notification) {
      case 'Failed':
        return `${label} after ${attempts} ${plural(attempts, 'attempt')}`;
      case 'Sent':
        return attempts > 1 ? `${label} after ${attempts} attempts` : label;
      default:
        return attempts > 0 ? `${label} · attempt ${attempts}` : label;
    }
  });
}

function plural(count: number, noun: string): string {
  return count === 1 ? noun : `${noun}s`;
}
