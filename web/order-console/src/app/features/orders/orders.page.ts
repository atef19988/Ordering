import { ChangeDetectionStrategy, Component, signal, viewChild } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { UiButtonComponent } from '../../lib/ui/ui-button.component';
import { UiFieldComponent } from '../../lib/ui/ui-field.component';
import { OrderCreateFormComponent } from './forms/order-create-form.component';
import { ORDER_ID_PATTERN, OrderDetail } from './models/order.models';
import { OrderDetailView } from './views/order-detail.view';
import { StockPanelView } from './views/stock-panel.view';

/**
 * The console: the order form in the action column, the lookup + live detail in the fluid
 * column, the paged stock panel across the bottom (layout classes live in `styles.scss`).
 * The page only wires outcomes together: a placed order goes to the detail column, and every
 * event that moved stock — a create, a cancel, a stock conflict — reloads the panel's current page.
 */
@Component({
  selector: 'app-orders-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    UiFieldComponent,
    UiButtonComponent,
    OrderCreateFormComponent,
    OrderDetailView,
    StockPanelView,
  ],
  template: `
    <div class="console">
      <aside class="console__action" aria-labelledby="new-order">
        <h2 id="new-order" class="section-title">New order</h2>
        <app-order-create-form (placed)="onPlaced($event)" (stockChanged)="stock().reload()" />
      </aside>

      <main class="console__detail" aria-labelledby="order-detail">
        <h2 id="order-detail" class="section-title">Order</h2>
        <!-- A bare form (no [formGroup]) has no ngSubmit; the native event does the job. -->
        <form class="lookup" (submit)="lookUp($event)" novalidate>
          <ui-field label="Order id" for="lookup-id" [error]="lookupError()">
            <input
              id="lookup-id"
              class="control control--data"
              [formControl]="lookup"
              inputmode="numeric"
              autocomplete="off"
              spellcheck="false"
              placeholder="Paste a 19-digit order id"
            />
          </ui-field>
          <ui-button type="submit">Look up</ui-button>
        </form>
        <app-order-detail [orderId]="orderId()" [seed]="seed()" (cancelled)="stock().reload()" />
      </main>

      <section class="console__stock stock" aria-labelledby="stock">
        <h2 id="stock" class="section-title">Stock</h2>
        <app-stock-panel />
      </section>
    </div>
  `,
  styles: `
    .lookup {
      display: flex;
      align-items: flex-start;
      gap: var(--s-2);
      max-width: 480px;
      margin-top: var(--s-3);
    }

    .lookup ui-field {
      flex: 1;
      margin-bottom: 0;
    }

    .lookup ui-button {
      /* Sit on the control's row: label height + its gap. */
      margin-top: calc(var(--t-sm) * 1.45 + var(--s-1));
    }

    /* The band is a strip in the shell; the panel needs the full width. */
    .stock {
      display: block;
    }
  `,
})
export class OrdersPage {
  protected readonly orderId = signal<string | null>(null);
  protected readonly seed = signal<OrderDetail | null>(null);
  protected readonly lookupError = signal<string | null>(null);
  protected readonly lookup = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(ORDER_ID_PATTERN)],
  });

  protected readonly stock = viewChild.required(StockPanelView);

  protected onPlaced(order: OrderDetail): void {
    this.seed.set(order);
    this.orderId.set(order.id);
    this.lookup.setValue(order.id);
    this.lookupError.set(null);
    this.stock().reload();
  }

  protected lookUp(event: Event): void {
    event.preventDefault();
    const id = this.lookup.value.trim();
    if (this.lookup.invalid) {
      this.lookupError.set(id === '' ? 'Paste an order id.' : 'An order id is digits only.');
      return;
    }
    this.lookupError.set(null);
    this.seed.set(null);
    this.orderId.set(id);
  }
}
