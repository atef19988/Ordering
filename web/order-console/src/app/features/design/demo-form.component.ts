import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Observable, delay, map, of, switchMap, throwError } from 'rxjs';
import { ApiError } from '../../lib/api/api-error';
import { BaseFormComponent } from '../../lib/forms/base-form.component';
import { UiAlertComponent } from '../../lib/ui/ui-alert.component';
import { UiButtonComponent } from '../../lib/ui/ui-button.component';
import { UiFieldComponent } from '../../lib/ui/ui-field.component';

interface DemoResult {
  readonly reference: string;
}

/**
 * Exercises `BaseFormComponent` without an API: validation messages through `ui-field`, the
 * double-submit guard, the busy button, and a server error rendered by `ui-alert`. The form
 * only declares controls and `perform()`; every flag comes from the base class.
 */
@Component({
  selector: 'app-demo-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, UiFieldComponent, UiButtonComponent, UiAlertComponent],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
      <ui-field label="Customer reference" for="demo-ref" [error]="errorFor('reference')">
        <input id="demo-ref" class="control control--data" formControlName="reference" autocomplete="off" />
      </ui-field>

      <ui-field label="Quantity" for="demo-qty" hint="Whole units." [error]="errorFor('quantity')">
        <input id="demo-qty" class="control control--data" type="number" formControlName="quantity" />
      </ui-field>

      <label class="check">
        <input type="checkbox" formControlName="fail" />
        Fail with a stock conflict
      </label>

      @if (serverError(); as error) {
        <ui-alert tone="stop" [dismissible]="true" (dismissed)="serverError.set(null)">
          {{ error.message }}
        </ui-alert>
      }
      @if (submitted() && !serverError()) {
        <ui-alert tone="ok">Order placed</ui-alert>
      }

      <div class="actions">
        <ui-button type="submit" variant="primary" [busy]="submitting()">Place order</ui-button>
        <ui-button (pressed)="resetForm(initial)">Reset</ui-button>
      </div>
    </form>
  `,
  styles: `
    form {
      max-width: 380px;
    }

    .check {
      display: flex;
      align-items: center;
      gap: var(--s-2);
      margin-bottom: var(--s-4);
      font-size: var(--t-sm);
    }

    ui-alert {
      margin-bottom: var(--s-4);
    }

    .actions {
      display: flex;
      gap: var(--s-2);
    }
  `,
})
export class DemoFormComponent extends BaseFormComponent<DemoResult> {
  protected readonly initial = { reference: '', quantity: 1, fail: false };

  readonly form = inject(FormBuilder).nonNullable.group({
    reference: [this.initial.reference, [Validators.required, Validators.maxLength(64)]],
    quantity: [this.initial.quantity, [Validators.required, Validators.min(1)]],
    fail: [this.initial.fail],
  });

  protected perform(): Observable<DemoResult> {
    const { reference, fail } = this.form.getRawValue();
    return of(null).pipe(
      delay(900),
      switchMap(() =>
        fail
          ? throwError(
              () =>
                new ApiError(
                  'stock.insufficient',
                  'Only 1 left of SKU-001. Lower the quantity or pick another product.',
                  409,
                  { productCode: 'SKU-001', available: 1 },
                ),
            )
          : of(null),
      ),
      map(() => ({ reference })),
    );
  }
}
