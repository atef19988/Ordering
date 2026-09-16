import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormArray,
  FormBuilder,
  FormControl,
  FormGroup,
  PristineChangeEvent,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { Observable, filter } from 'rxjs';
import { ApiError } from '../../../lib/api/api-error';
import { BaseFormComponent, DEFAULT_MESSAGES } from '../../../lib/forms/base-form.component';
import { UiAlertComponent, UiAlertTone } from '../../../lib/ui/ui-alert.component';
import { UiButtonComponent } from '../../../lib/ui/ui-button.component';
import { UiFieldComponent } from '../../../lib/ui/ui-field.component';
import { Countdown } from '../../../lib/util/countdown';
import { newKey } from '../../../lib/util/idempotency-key';
import { OrderDetail } from '../models/order.models';
import { OrdersApi } from '../services/orders.api';
import { ProductPickerComponent } from './product-picker.component';

type LineGroup = FormGroup<{
  productCode: FormControl<string | null>;
  quantity: FormControl<number>;
}>;

/** What the form-level alert says for one failed submit, and which action it offers. */
interface Problem {
  readonly tone: UiAlertTone;
  readonly message: string;
  readonly action: 'retry' | 'new-order' | null;
}

const CUSTOMER_REFERENCE_MAX_LENGTH = 64;
const PRODUCT_CODE_MAX_LENGTH = 32;

/** The server's rule too: one line per product; merge quantities instead of repeating a code. */
function distinctProductCodes(control: AbstractControl): ValidationErrors | null {
  const codes = (control as FormArray<LineGroup>).controls
    .map((line) => line.controls.productCode.value?.trim().toUpperCase())
    .filter((code): code is string => !!code);
  const duplicate = codes.find((code, index) => codes.indexOf(code) !== index);
  return duplicate === undefined ? null : { duplicateProduct: { code: duplicate } };
}

/** `productCode` from the ProblemDetails extensions, else the `'…'`-quoted code in the server's message. */
function productCodeOf(error: ApiError): string | null {
  return error.extension<string>('productCode') ?? /'([^']+)'/.exec(error.message)?.[1] ?? null;
}

/**
 * Customer reference + repeatable lines → `POST /api/orders`. The base class owns submitting /
 * serverError / submitted; this component owns the lines, the key policy below and the copy
 * for each failure the API can answer with.
 */
@Component({
  selector: 'app-order-create-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    UiFieldComponent,
    UiButtonComponent,
    UiAlertComponent,
    ProductPickerComponent,
  ],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
      <ui-field label="Customer reference" for="customer-reference" [error]="errorFor('customerReference')">
        <input
          id="customer-reference"
          class="control control--data"
          formControlName="customerReference"
          autocomplete="off"
          spellcheck="false"
          maxlength="64"
        />
      </ui-field>

      <fieldset class="lines" formArrayName="lines">
        <legend class="section-title">Lines</legend>
        @for (line of lines.controls; track line; let i = $index) {
          <div class="line" [formGroupName]="i">
            <ui-field class="line__product" label="Product" [for]="'line-' + i + '-product'" [error]="errorFor(line.controls.productCode)">
              <app-product-picker [id]="'line-' + i + '-product'" formControlName="productCode" />
            </ui-field>
            <ui-field class="line__qty" label="Qty" [for]="'line-' + i + '-quantity'" [error]="errorFor(line.controls.quantity)">
              <input
                [id]="'line-' + i + '-quantity'"
                class="control control--data"
                type="number"
                inputmode="numeric"
                min="1"
                step="1"
                formControlName="quantity"
              />
            </ui-field>
            <button
              type="button"
              class="line__remove"
              [attr.aria-label]="'Remove line ' + (i + 1)"
              [disabled]="lines.length === 1 || submitting()"
              (click)="removeLine(i)"
            >
              <span aria-hidden="true">✕</span>
            </button>
          </div>
        }
        @if (duplicateProduct(); as code) {
          <p class="lines__error" role="alert">
            <span aria-hidden="true">✕</span> {{ code }} appears twice. Merge the quantities into one line.
          </p>
        }
        <ui-button [disabled]="submitting()" (pressed)="addLine()">+ Add line</ui-button>
      </fieldset>

      @if (problem(); as problem) {
        <ui-alert [tone]="problem.tone" [dismissible]="problem.action === null" (dismissed)="serverError.set(null)">
          {{ problem.message }}
          @if (problem.action === 'retry') {
            <ui-button slot="actions" [disabled]="retryAfter.active()" [busy]="submitting()" (pressed)="submit()">
              Retry with the same key{{ retryAfter.active() ? ' (' + retryAfter.remaining() + ' s)' : '' }}
            </ui-button>
          }
          @if (problem.action === 'new-order') {
            <ui-button slot="actions" (pressed)="newOrder()">New order</ui-button>
          }
        </ui-alert>
      }
      @if (placedOrder(); as placed) {
        <ui-alert tone="ok" title="Order placed" [dismissible]="true" (dismissed)="placedOrder.set(null)">
          <span class="data">{{ placed.id }}</span> · total {{ money(placed.total) }}
        </ui-alert>
      }

      <div class="actions">
        <ui-button type="submit" variant="primary" [busy]="submitting()" [disabled]="retryAfter.active()">
          Place order
        </ui-button>
        <ui-button [disabled]="submitting()" (pressed)="newOrder()">New order</ui-button>
      </div>

      <!-- The key is visible on purpose: a retry with the same key is the whole point, and a
           reviewer (or a support call) can match it against the server's idempotency row. -->
      <p class="key muted">
        Idempotency key <span class="data key__value">{{ idempotencyKey() ?? '—' }}</span>
      </p>
    </form>
  `,
  styles: `
    form {
      margin-top: var(--s-4);
    }

    .lines {
      margin: 0 0 var(--s-4);
      padding: 0;
      border: 0;
    }

    .lines legend {
      margin-bottom: var(--s-3);
      padding: 0;
    }

    .line {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 72px 28px;
      gap: var(--s-2);
      align-items: start;
    }

    .line ui-field {
      margin-bottom: var(--s-2);
    }

    .line__remove {
      align-self: start;
      width: 28px;
      height: 36px;
      /* Sit on the control's row: label height + its gap. */
      margin-top: calc(var(--t-sm) * 1.45 + var(--s-1));
      border: 0;
      border-radius: var(--r-sm);
      background: transparent;
      color: var(--c-ink-muted);
      cursor: pointer;
    }

    .line__remove:hover:not(:disabled) {
      background: var(--c-line);
      color: var(--c-ink);
    }

    .line__remove:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }

    .lines__error {
      margin-bottom: var(--s-3);
      font-size: var(--t-sm);
      color: var(--c-stop);
    }

    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--s-2);
    }

    .key {
      margin-top: var(--s-3);
      font-size: var(--t-xs);
    }

    .key__value {
      overflow-wrap: anywhere;
    }

    ui-alert {
      margin-bottom: var(--s-4);
    }

    .muted {
      color: var(--c-ink-muted);
    }
  `,
})
export class OrderCreateFormComponent extends BaseFormComponent<OrderDetail> {
  /** A `201` (or a `200` replay) — the page shows it in the detail column and reloads the stock panel. */
  readonly placed = output<OrderDetail>();
  /** The server reported stock the panel does not show (`409 stock.insufficient`); the page reloads the panel. */
  readonly stockChanged = output<void>();

  /**
   * Idempotency-Key policy — one key per order *attempt*, never per request.
   *
   * The key is generated once, the first time the form becomes dirty, and is sent with the
   * first submit and with every retry of it: a network error, a timeout, `409
   * idempotency.in_progress` and every `503` (`stock.busy`, `server.busy`, `server.timeout`) —
   * exactly the cases where `ApiError.isRetryableWithSameKey` is true and the server may or may
   * not have committed. Reusing the key is what turns an uncertain submit into one order, not two.
   *
   * A fresh key is issued only after a successful submit or when the user presses `New order`.
   * Editing the lines does *not* rotate it: the server then answers `409 idempotency.key_reuse`
   * ("that key was used for a different order"), which the alert explains — the key is doing
   * its job, and the user starts a new order to get a new one.
   */
  readonly idempotencyKey = signal<string | null>(null);

  readonly retryAfter = new Countdown();
  readonly placedOrder = signal<OrderDetail | null>(null);

  private readonly fb = inject(FormBuilder);
  private readonly api = inject(OrdersApi);

  readonly form = this.fb.group({
    customerReference: this.fb.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(CUSTOMER_REFERENCE_MAX_LENGTH),
    ]),
    lines: this.fb.array<LineGroup>([this.newLine()], [distinctProductCodes]),
  });

  protected override readonly messages = {
    ...DEFAULT_MESSAGES,
    unknownProduct: () => 'No product with that code',
  };

  /** Re-evaluated on every value change, so the message follows the array's validator. */
  protected readonly duplicateProduct = signal<string | null>(null);

  /** The alert for the last failed submit; `null` when the failure was rendered inline instead. */
  protected readonly problem = signal<Problem | null>(null);

  constructor() {
    super();
    const destroyRef = inject(DestroyRef);

    this.form.events
      .pipe(
        filter((event) => event instanceof PristineChangeEvent && !event.pristine),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe(() => this.idempotencyKey.update((key) => key ?? newKey()));

    this.lines.statusChanges.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => {
      const duplicate = this.lines.errors?.['duplicateProduct'] as { code: string } | undefined;
      this.duplicateProduct.set(duplicate?.code ?? null);
    });

    // `describe()` has side effects (countdown, inline errors, the stock hint), so it runs in an
    // effect rather than a computed.
    effect(() => {
      const error = this.serverError();
      untracked(() => this.problem.set(error === null ? null : this.describe(error)));
    });
  }

  get lines(): FormArray<LineGroup> {
    return this.form.controls.lines;
  }

  protected money(value: number): string {
    return value.toFixed(2);
  }

  addLine(): void {
    this.lines.push(this.newLine());
    this.form.markAsDirty();
  }

  removeLine(index: number): void {
    if (this.lines.length > 1) {
      this.lines.removeAt(index);
      this.form.markAsDirty();
    }
  }

  /** Back to an empty form with no key; the next edit issues a fresh one. */
  newOrder(): void {
    this.retryAfter.stop();
    while (this.lines.length > 1) {
      this.lines.removeAt(this.lines.length - 1);
    }
    this.resetForm({ customerReference: '', lines: [{ productCode: null, quantity: 1 }] });
    this.placedOrder.set(null);
    this.idempotencyKey.set(null);
  }

  protected perform(): Observable<OrderDetail> {
    this.retryAfter.stop();
    this.placedOrder.set(null);
    // A pristine form (a repeat of the last placed order) has no key yet: issue one now.
    const key = this.idempotencyKey() ?? newKey();
    this.idempotencyKey.set(key);

    const { customerReference, lines } = this.form.getRawValue();
    return this.api.create(
      {
        customerReference: customerReference.trim(),
        lines: lines.map((line) => ({
          productCode: (line.productCode ?? '').trim(),
          quantity: Number(line.quantity),
        })),
      },
      key,
    );
  }

  protected override onSuccess(order: OrderDetail): void {
    this.placedOrder.set(order);
    this.placed.emit(order);
    // The attempt is over: the next edit (or a plain re-submit) gets a fresh key.
    this.idempotencyKey.set(null);
    this.form.markAsPristine();
  }

  /** Turns one `ApiError` into alert copy — and, for the retryable ones, starts the countdown. */
  private describe(error: ApiError): Problem | null {
    const code = productCodeOf(error);

    if (error.isRetryableWithSameKey) {
      this.retryAfter.start(error.retryAfterSeconds);
      const message =
        error.code === 'idempotency.in_progress'
          ? 'Still processing. Retry with the same key.'
          : error.code === 'stock.busy'
            ? `High demand on ${code ?? 'this product'}. Retry with the same key.`
            : error.status === 0
              ? `${error.message} Retry with the same key.`
              : 'The service is busy. Retry with the same key.';
      return { tone: 'wait', message, action: 'retry' };
    }

    switch (error.code) {
      case 'stock.insufficient': {
        const available = Number(error.extension<number | string>('available') ?? 0);
        this.stockChanged.emit();
        return {
          tone: 'stop',
          message:
            available > 0
              ? `Only ${available} left of ${code}. Lower the quantity or pick another product.`
              : `${code} is sold out.`,
          action: null,
        };
      }
      case 'idempotency.key_reuse':
        return {
          tone: 'stop',
          message: 'That key was used for a different order. Start a new order to get a new key.',
          action: 'new-order',
        };
      case 'product.unknown': {
        const line = this.lines.controls.find(
          (candidate) => candidate.controls.productCode.value?.trim().toUpperCase() === code?.toUpperCase(),
        );
        if (line) {
          line.controls.productCode.setErrors({ unknownProduct: true });
          line.controls.productCode.markAsTouched();
          return null;
        }
        return { tone: 'stop', message: error.message, action: null };
      }
      default:
        return { tone: 'stop', message: error.message, action: null };
    }
  }

  private newLine(): LineGroup {
    return this.fb.group({
      productCode: this.fb.control<string | null>(null, [
        Validators.required,
        Validators.maxLength(PRODUCT_CODE_MAX_LENGTH),
      ]),
      quantity: this.fb.nonNullable.control(1, [Validators.required, Validators.min(1)]),
    });
  }
}
