import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StatusTone } from './status';

export type UiAlertTone = StatusTone | 'info';

/** The word that accompanies each tone, so an alert is never colour alone. */
const TONE_WORDS: Readonly<Record<UiAlertTone, string>> = {
  info: 'Note',
  ok: 'Done',
  wait: 'Waiting',
  stop: 'Problem',
  void: 'Cancelled',
};

/**
 * A form-level or page-level message: tone bar, tone word, projected copy, optional actions
 * (`slot="actions"`) and an optional dismiss. `stop` is announced immediately (`role="alert"`),
 * everything else politely (`role="status"`).
 */
@Component({
  selector: 'ui-alert',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"alert tone-" + tone()', '[attr.role]': 'role()' },
  template: `
    <div class="body">
      <p class="heading">
        <span class="word">{{ title() ?? word() }}</span>
      </p>
      <div class="copy prose"><ng-content /></div>
      <div class="actions"><ng-content select="[slot=actions]" /></div>
    </div>
    @if (dismissible()) {
      <button type="button" class="dismiss" (click)="dismissed.emit()" aria-label="Dismiss">
        <span aria-hidden="true">✕</span>
      </button>
    }
  `,
  styles: `
    :host {
      display: flex;
      gap: var(--s-3);
      padding: var(--s-3) var(--s-4);
      border: 1px solid var(--c-line);
      border-left: 4px solid var(--c-ink-muted);
      border-radius: var(--r-sm);
      background: var(--c-surface);
      font-size: var(--t-sm);
    }

    .body {
      flex: 1;
      min-width: 0;
    }

    .heading {
      font-weight: 600;
    }

    .copy:not(:empty) {
      margin-top: var(--s-1);
    }

    .actions:not(:empty) {
      display: flex;
      flex-wrap: wrap;
      gap: var(--s-2);
      margin-top: var(--s-3);
    }

    .dismiss {
      flex: none;
      align-self: flex-start;
      width: 28px;
      height: 28px;
      border: 0;
      border-radius: var(--r-sm);
      background: transparent;
      color: var(--c-ink-muted);
      cursor: pointer;
    }

    .dismiss:hover {
      background: var(--c-panel);
    }

    .tone-ok {
      border-left-color: var(--c-ok);
    }

    .tone-ok .word {
      color: var(--c-ok);
    }

    .tone-wait {
      border-left-color: var(--c-wait);
    }

    .tone-wait .word {
      color: var(--c-wait);
    }

    .tone-stop {
      border-left-color: var(--c-stop);
    }

    .tone-stop .word {
      color: var(--c-stop);
    }

    .tone-void {
      border-left-color: var(--c-void);
    }

    .tone-void .word {
      color: var(--c-void);
    }
  `,
})
export class UiAlertComponent {
  readonly tone = input<UiAlertTone>('info');
  /** Replaces the tone word (`Problem`, `Done`, …) with a specific heading. */
  readonly title = input<string | null>(null);
  readonly dismissible = input(false);

  readonly dismissed = output<void>();

  protected readonly word = computed(() => TONE_WORDS[this.tone()]);
  protected readonly role = computed(() => (this.tone() === 'stop' ? 'alert' : 'status'));
}
