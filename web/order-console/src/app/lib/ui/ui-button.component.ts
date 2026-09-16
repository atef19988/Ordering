import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { UiSpinnerComponent } from './ui-spinner.component';

export type UiButtonVariant = 'primary' | 'secondary' | 'danger';

/**
 * The console's one button. `busy` shows a spinner and blocks a second press, so a form never
 * needs its own guard; `danger` is reserved for cancel. Inside a `<form>`, `type="submit"`
 * triggers `ngSubmit` like a native button would.
 */
@Component({
  selector: 'ui-button',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [UiSpinnerComponent],
  template: `
    <button
      class="btn btn--{{ variant() }}"
      [type]="type()"
      [disabled]="inert()"
      [attr.aria-busy]="busy() || null"
      (click)="pressed.emit()"
    >
      @if (busy()) {
        <ui-spinner label="Working" />
      }
      <span class="label"><ng-content /></span>
    </button>
  `,
  styles: `
    :host {
      display: inline-block;
    }

    :host(.block),
    :host(.block) .btn {
      display: block;
      width: 100%;
    }

    .btn {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: var(--s-2);
      min-height: 36px;
      padding: var(--s-2) var(--s-4);
      border: 1px solid var(--c-ink);
      border-radius: var(--r-sm);
      background: var(--c-surface);
      color: var(--c-ink);
      font-weight: 600;
      cursor: pointer;
      white-space: nowrap;
    }

    .btn--primary {
      background: var(--c-ink);
      color: var(--c-surface);
    }

    .btn--primary ui-spinner {
      color: var(--c-surface);
    }

    .btn--danger {
      border-color: var(--c-stop);
      color: var(--c-stop);
    }

    .btn:disabled {
      cursor: not-allowed;
      opacity: 0.55;
    }
  `,
})
export class UiButtonComponent {
  readonly variant = input<UiButtonVariant>('secondary');
  readonly type = input<'button' | 'submit'>('button');
  readonly disabled = input(false);
  readonly busy = input(false);

  /** Fires on a real press only — never while disabled or busy. */
  readonly pressed = output<void>();

  protected readonly inert = computed(() => this.disabled() || this.busy());
}
