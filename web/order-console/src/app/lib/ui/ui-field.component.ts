import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
} from '@angular/core';

/**
 * Label + projected native control + hint + error. The error region is always in the DOM with
 * `aria-live="polite"`, so a message is announced when it appears. The projected control gets
 * `aria-invalid` and `aria-describedby` wired for it, so callers only write
 * `<input class="control" id="…" formControlName="…">`.
 */
@Component({
  selector: 'ui-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label class="label" [attr.for]="for()">
      {{ label() }}
      @if (optional()) {
        <span class="muted">(optional)</span>
      }
    </label>
    <ng-content />
    @if (hint(); as hint) {
      <p class="hint" [id]="hintId()">{{ hint }}</p>
    }
    <p class="error" [id]="errorId()" aria-live="polite">
      @if (error(); as error) {
        <span aria-hidden="true">✕</span> {{ error }}
      }
    </p>
  `,
  styles: `
    :host {
      display: block;
      margin-bottom: var(--s-4);
    }

    .label {
      display: block;
      margin-bottom: var(--s-1);
      font-size: var(--t-sm);
      font-weight: 600;
    }

    .muted {
      font-weight: 400;
      color: var(--c-ink-muted);
    }

    .hint,
    .error {
      margin-top: var(--s-1);
      font-size: var(--t-sm);
    }

    .hint {
      color: var(--c-ink-muted);
    }

    .error {
      min-height: 1.25em;
      color: var(--c-stop);
    }
  `,
})
export class UiFieldComponent {
  readonly label = input.required<string>();
  /** The `id` of the projected control; the label points at it and the error describes it. */
  readonly for = input.required<string>();
  readonly hint = input<string | null>(null);
  readonly error = input<string | null>(null);
  readonly optional = input(false);

  protected readonly hintId = computed(() => `${this.for()}-hint`);
  protected readonly errorId = computed(() => `${this.for()}-error`);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    afterRenderEffect(() => {
      const control = this.host.nativeElement.querySelector(`#${CSS.escape(this.for())}`);
      if (!control) {
        return;
      }
      const invalid = this.error() !== null;
      const describedBy = [this.hint() ? this.hintId() : null, invalid ? this.errorId() : null]
        .filter((id): id is string => id !== null)
        .join(' ');

      control.setAttribute('aria-invalid', String(invalid));
      if (describedBy) {
        control.setAttribute('aria-describedby', describedBy);
      } else {
        control.removeAttribute('aria-describedby');
      }
    });
  }
}
