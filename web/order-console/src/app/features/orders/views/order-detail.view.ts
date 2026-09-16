import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiError } from '../../../lib/api/api-error';
import { APP_CONFIG } from '../../../core/config/app.config';
import { BaseResource } from '../../../lib/state/base-resource';
import { UiAlertComponent } from '../../../lib/ui/ui-alert.component';
import { UiBadgeComponent } from '../../../lib/ui/ui-badge.component';
import { UiButtonComponent } from '../../../lib/ui/ui-button.component';
import { UiSpinnerComponent } from '../../../lib/ui/ui-spinner.component';
import { UiStatusBandComponent } from '../../../lib/ui/ui-status-band.component';
import { UiColumn, UiTableComponent } from '../../../lib/ui/ui-table.component';
import { Countdown } from '../../../lib/util/countdown';
import { OrderDetail, OrderLine, isFinal } from '../models/order.models';
import { OrderStream } from '../services/order-stream';
import { OrdersApi } from '../services/orders.api';

const money = (value: number): string => value.toFixed(2);

const LINE_COLUMNS: readonly UiColumn<OrderLine>[] = [
  { field: 'productCode', header: 'Code', data: true, cell: (line) => line.productCode },
  { field: 'quantity', header: 'Qty', numeric: true, cell: (line) => String(line.quantity) },
  { field: 'unitPrice', header: 'Unit', numeric: true, cell: (line) => money(line.unitPrice) },
  { field: 'lineTotal', header: 'Amount', numeric: true, cell: (line) => money(line.lineTotal) },
];

/**
 * One order, live. A new `orderId` shows the `seed` (a `POST` body) or fetches once, then opens
 * `OrderStream`; every frame replaces the value, so nothing polls while the stream is up. If
 * the stream cannot be opened (`503 sse.full`, an API without Task 13, or the browser gave up)
 * the view reloads every `fallbackPollMs` and says so. `done` closes the stream: the view is
 * final. Cancel goes through a confirm dialog; the stream — not a reload — delivers the result.
 */
@Component({
  selector: 'app-order-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    UiStatusBandComponent,
    UiBadgeComponent,
    UiTableComponent,
    UiButtonComponent,
    UiAlertComponent,
    UiSpinnerComponent,
  ],
  template: `
    @if (orderId() === null) {
      <p class="prose muted empty">Look up an order by id, or place one.</p>
    } @else if (order.initial() && order.loading()) {
      <p class="empty"><ui-spinner size="md" label="Loading order" [showLabel]="true" /></p>
    } @else if (order.data(); as detail) {
      <header class="head">
        <h3 class="id data">{{ detail.id }}</h3>
        <ui-badge [status]="detail.status" />
        <span class="live muted">
          @if (stream.connected()) {
            <span class="dot dot--ok" aria-hidden="true"></span> Live
          } @else if (final()) {
            Final
          } @else if (polling()) {
            Live updates unavailable — refreshing every {{ pollSeconds }} s
          } @else {
            <ui-spinner label="Connecting to live updates" /> Connecting
          }
        </span>
      </header>

      <ui-status-band
        class="band"
        [status]="bandStatus()"
        [notification]="detail.status === 'Cancelled' ? detail.notificationStatus : null"
        [attempts]="detail.notificationAttempts"
      />
      @if (detail.notificationStatus === 'Failed') {
        <p class="failed" role="status">
          Customer notification failed after {{ detail.notificationAttempts }}
          {{ detail.notificationAttempts === 1 ? 'attempt' : 'attempts' }}.
        </p>
      }

      <dl class="facts">
        <div><dt>Customer</dt><dd class="data">{{ detail.customerReference }}</dd></div>
        <div><dt>Created</dt><dd class="data">{{ when(detail.createdAt) }}</dd></div>
        @if (detail.cancelledAt; as cancelledAt) {
          <div><dt>Cancelled</dt><dd class="data">{{ when(cancelledAt) }}</dd></div>
        }
      </dl>

      <ui-table
        caption="Order lines"
        [columns]="columns"
        [rows]="detail.lines"
        [rowKey]="lineKey"
        [footer]="['total', '', '', money(detail.total)]"
      />

      @if (cancelError(); as error) {
        <ui-alert tone="wait" class="block" [dismissible]="!error.isRetryableWithSameKey" (dismissed)="cancelError.set(null)">
          {{ error.message }}
          @if (error.isRetryableWithSameKey) {
            <ui-button slot="actions" [disabled]="retryAfter.active()" [busy]="cancelling()" (pressed)="performCancel()">
              Retry{{ retryAfter.active() ? ' (' + retryAfter.remaining() + ' s)' : '' }}
            </ui-button>
          }
        </ui-alert>
      }

      @if (detail.status !== 'Cancelled') {
        <div class="actions">
          <ui-button variant="danger" [busy]="cancelling()" (pressed)="askToCancel()">Cancel order</ui-button>
        </div>
      }

      @if (order.error(); as error) {
        <p class="stale muted" role="status">Last refresh failed — {{ error.message }}</p>
      }

      <dialog #confirm class="confirm" aria-labelledby="confirm-title">
        <h3 id="confirm-title" class="confirm__title">Cancel this order?</h3>
        <p class="prose">Its stock goes back to the catalogue. This cannot be undone.</p>
        <div class="confirm__actions">
          <ui-button (pressed)="closeConfirm()">Keep order</ui-button>
          <ui-button variant="danger" (pressed)="performCancel()">Cancel order</ui-button>
        </div>
      </dialog>
    } @else if (order.error(); as error) {
      <ui-alert tone="stop" class="block" [title]="error.status === 404 ? 'Order not found' : null">
        {{ error.status === 404 ? 'No order with id ' + orderId() + '.' : error.message }}
        @if (error.status !== 404) {
          <ui-button slot="actions" (pressed)="order.reload()">Try again</ui-button>
        }
      </ui-alert>
    }
  `,
  styles: `
    :host {
      display: block;
    }

    .empty,
    .block {
      display: block;
      margin-top: var(--s-3);
    }

    .head {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--s-2) var(--s-4);
      margin: var(--s-3) 0 var(--s-3);
    }

    .id {
      font-size: var(--t-lg);
      font-weight: 600;
      overflow-wrap: anywhere;
    }

    .live {
      display: inline-flex;
      align-items: center;
      gap: var(--s-2);
      margin-left: auto;
      font-size: var(--t-sm);
    }

    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--c-ok);
    }

    .band {
      margin-bottom: var(--s-3);
    }

    .failed {
      margin: calc(-1 * var(--s-1)) 0 var(--s-3);
      font-size: var(--t-sm);
      font-weight: 600;
      color: var(--c-stop);
    }

    .facts {
      display: flex;
      flex-wrap: wrap;
      gap: var(--s-2) var(--s-8);
      margin: 0 0 var(--s-4);
      font-size: var(--t-sm);
    }

    .facts dt {
      font-size: var(--t-xs);
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--c-ink-muted);
    }

    .facts dd {
      margin: 0;
    }

    .actions {
      margin-top: var(--s-4);
    }

    .stale {
      margin-top: var(--s-3);
      font-size: var(--t-sm);
    }

    .confirm {
      max-width: 420px;
      padding: var(--s-6);
      border: 1px solid var(--c-ink);
      border-radius: var(--r-md);
      color: var(--c-ink);
      background: var(--c-surface);
    }

    .confirm::backdrop {
      background: rgb(15 36 52 / 0.4);
    }

    .confirm__title {
      margin-bottom: var(--s-2);
      font-size: var(--t-lg);
      font-weight: 600;
    }

    .confirm__actions {
      display: flex;
      justify-content: flex-end;
      gap: var(--s-2);
      margin-top: var(--s-6);
    }

    .muted {
      color: var(--c-ink-muted);
    }
  `,
})
export class OrderDetailView {
  readonly orderId = input<string | null>(null);
  /** The order as a `POST` just returned it — shown at once instead of a first `GET`. */
  readonly seed = input<OrderDetail | null>(null);
  /** The cancel was accepted; the page reloads the stock panel. */
  readonly cancelled = output<OrderDetail>();

  private readonly api = inject(OrdersApi);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly pollSeconds = Math.round(inject(APP_CONFIG).fallbackPollMs / 1000);

  protected readonly stream = new OrderStream();
  protected readonly order = new BaseResource<OrderDetail>(() =>
    this.api.getById(this.orderId() ?? ''),
  );
  protected readonly cancelling = signal(false);
  protected readonly cancelError = signal<ApiError | null>(null);
  protected readonly polling = signal(false);
  protected readonly retryAfter = new Countdown();

  protected readonly final = computed(() => {
    const order = this.order.data();
    return this.stream.done() || (order !== null && isFinal(order));
  });

  /** Grey once cancelled; otherwise the notification drives the colour: amber pending, green sent, red failed. */
  protected readonly bandStatus = computed(() => {
    const order = this.order.data();
    return order === null || order.status === 'Cancelled' ? 'Cancelled' : order.notificationStatus;
  });

  protected readonly columns = LINE_COLUMNS;
  protected readonly lineKey = (line: OrderLine): string => line.productCode;

  private readonly confirm = viewChild<ElementRef<HTMLDialogElement>>('confirm');
  private pollHandle: ReturnType<typeof setInterval> | null = null;

  constructor() {
    const pollMs = inject(APP_CONFIG).fallbackPollMs;

    // A new id: show what is known, fetch once otherwise, then go live.
    effect(() => {
      const id = this.orderId();
      untracked(() => this.show(id, this.seed()));
    });

    // Every frame is the whole order; the stream, not a request, keeps the view current.
    effect(() => {
      const frame = this.stream.data();
      if (frame !== null) {
        untracked(() => this.order.set(frame));
      }
    });

    // Refused or dropped for good → slow poll with a note. Done or final → nothing more to wait for.
    effect(() => {
      const shown = this.orderId() !== null;
      const refused = this.stream.error() !== null;
      const final = this.final();
      const notFound = this.order.error()?.status === 404;
      untracked(() => {
        if (shown && refused && !final && !notFound) {
          this.startPolling(pollMs);
        } else {
          this.stopPolling();
        }
      });
    });

    this.destroyRef.onDestroy(() => this.stopPolling());
  }

  protected when(iso: string): string {
    return new Date(iso).toLocaleString();
  }

  protected money(value: number): string {
    return money(value);
  }

  protected askToCancel(): void {
    this.cancelError.set(null);
    this.confirm()?.nativeElement.showModal();
  }

  protected closeConfirm(): void {
    this.confirm()?.nativeElement.close();
  }

  protected performCancel(): void {
    const id = this.orderId();
    if (id === null || this.cancelling()) {
      return;
    }
    this.closeConfirm();
    this.retryAfter.stop();
    this.cancelling.set(true);
    this.cancelError.set(null);

    this.api
      .cancel(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (order) => {
          this.cancelling.set(false);
          // The response is already the truth; the stream's next frame says the same thing.
          this.order.set(order);
          this.cancelled.emit(order);
        },
        error: (error: unknown) => {
          const apiError = ApiError.from(error);
          this.cancelling.set(false);
          this.cancelError.set(apiError);
          this.retryAfter.start(apiError.retryAfterSeconds);
        },
      });
  }

  private show(id: string | null, seed: OrderDetail | null): void {
    this.stream.close();
    this.stopPolling();
    this.cancelError.set(null);
    this.retryAfter.stop();

    if (id === null) {
      this.order.clear();
      return;
    }
    if (seed !== null && seed.id === id) {
      this.order.set(seed);
    } else {
      this.order.clear();
      this.order.reload();
    }
    this.stream.openFor(id);
  }

  private startPolling(everyMs: number): void {
    if (this.pollHandle !== null) {
      return;
    }
    this.polling.set(true);
    this.pollHandle = setInterval(() => this.order.reload(), everyMs);
  }

  private stopPolling(): void {
    if (this.pollHandle !== null) {
      clearInterval(this.pollHandle);
      this.pollHandle = null;
    }
    this.polling.set(false);
  }
}
