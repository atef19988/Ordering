import { DestroyRef, Directive, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormGroup } from '@angular/forms';
import { Observable } from 'rxjs';
import { ApiError } from '../api/api-error';

export type ValidationMessage = (error: Record<string, unknown>) => string;

/** One sentence per validator key, in the copy voice of the design system. Subclasses may extend. */
export const DEFAULT_MESSAGES: Readonly<Record<string, ValidationMessage>> = {
  required: () => 'Required.',
  min: (e) => `Must be at least ${String(e['min'])}.`,
  max: (e) => `Must be at most ${String(e['max'])}.`,
  minlength: (e) => `Must be at least ${String(e['requiredLength'])} characters.`,
  maxlength: (e) => `Must be ${String(e['requiredLength'])} characters or fewer.`,
  pattern: () => 'Not in the expected format.',
};

/**
 * What every form repeats, written once: `submitting`, `serverError`, `submitted` signals, an
 * `errorFor()` helper for `ui-field`, and a `submit()` template method that touches every
 * control, refuses a double submission and routes the failure into `serverError`. Subclasses
 * define `form` and `perform()`; if a feature form re-implements a loading flag, this class is
 * wrong.
 */
@Directive()
export abstract class BaseFormComponent<TResult = void> {
  abstract readonly form: FormGroup;

  readonly submitting = signal(false);
  readonly serverError = signal<ApiError | null>(null);
  /** At least one `submit()` completed successfully since the last `resetForm()`. */
  readonly submitted = signal(false);

  protected readonly messages: Readonly<Record<string, ValidationMessage>> = DEFAULT_MESSAGES;

  private readonly destroyRef = inject(DestroyRef);

  submit(): void {
    if (this.submitting()) {
      return;
    }
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      return;
    }

    this.submitting.set(true);
    this.serverError.set(null);

    this.perform()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.submitting.set(false);
          this.submitted.set(true);
          this.onSuccess(result);
        },
        error: (error: unknown) => {
          this.submitting.set(false);
          this.serverError.set(ApiError.from(error));
        },
      });
  }

  /** The message for a control's first error, once the user has touched it; `null` when clean. */
  errorFor(control: AbstractControl | string): string | null {
    const target = typeof control === 'string' ? this.form.get(control) : control;
    if (!target || !target.touched || !target.errors) {
      return null;
    }
    const [key, detail] = Object.entries(target.errors)[0];
    const message = this.messages[key];
    return message ? message(asRecord(detail)) : 'Invalid value.';
  }

  /** Back to a pristine form: no server error, not submitted, controls untouched. */
  resetForm(value?: Record<string, unknown>): void {
    this.form.reset(value);
    this.serverError.set(null);
    this.submitted.set(false);
  }

  /** The one request this form makes. Runs only when the form is valid and nothing is in flight. */
  protected abstract perform(): Observable<TResult>;

  /** Hook for after a successful `perform()`; the default keeps the form as it is. */
  protected onSuccess(result: TResult): void {
    void result;
  }
}

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : {};
}
