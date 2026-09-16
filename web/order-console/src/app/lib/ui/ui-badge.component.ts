import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { statusView } from './status';

/**
 * A small inline state marker: a dot in the state's colour and the state's word. Reads the one
 * status map in `status.ts`; the `label` input overrides the word only, never the colour.
 */
@Component({
  selector: 'ui-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"badge tone-" + view().tone' },
  template: `
    <span class="dot" aria-hidden="true"></span>
    <span>{{ label() ?? view().label }}</span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--s-2);
      font-size: var(--t-sm);
      font-weight: 600;
      white-space: nowrap;
    }

    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: currentColor;
      flex: none;
    }

    .tone-ok {
      color: var(--c-ok);
    }

    .tone-wait {
      color: var(--c-wait);
    }

    .tone-stop {
      color: var(--c-stop);
    }

    .tone-void {
      color: var(--c-void);
    }
  `,
})
export class UiBadgeComponent {
  /** An API status (`Confirmed`, `Sent`, `Pending`, `Failed`, `Cancelled`, …) or a client outcome (`InsufficientStock`). */
  readonly status = input.required<string>();
  readonly label = input<string | null>(null);

  protected readonly view = computed(() => statusView(this.status()));
}
