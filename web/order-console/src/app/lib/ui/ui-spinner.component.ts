import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * An in-progress indicator. Announces its label to assistive technology; the ring stops
 * turning under `prefers-reduced-motion` and the visible label carries the meaning instead.
 */
@Component({
  selector: 'ui-spinner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'status', '[class]': '"spinner spinner--" + size()' },
  template: `
    <span class="ring" aria-hidden="true"></span>
    <span [class.visually-hidden]="!showLabel()">{{ label() }}</span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--s-2);
      color: var(--c-ink-muted);
      font-size: var(--t-sm);
      vertical-align: middle;
    }

    .ring {
      display: inline-block;
      width: 1em;
      height: 1em;
      border: 2px solid var(--c-line);
      border-top-color: currentColor;
      border-radius: 50%;
      animation: turn 0.8s linear infinite;
    }

    .spinner--md .ring {
      width: 1.5em;
      height: 1.5em;
    }

    @keyframes turn {
      to {
        transform: rotate(360deg);
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .ring {
        animation: none;
        border-style: dashed;
      }
    }
  `,
})
export class UiSpinnerComponent {
  readonly size = input<'sm' | 'md'>('sm');
  readonly label = input('Loading');
  /** Show the label next to the ring instead of only announcing it. */
  readonly showLabel = input(false);
}
