import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, switchMap, tap } from 'rxjs';
import { Sort, parseSort, sortParam } from '../../lib/api/paging';
import { UiAlertComponent } from '../../lib/ui/ui-alert.component';
import { UiBadgeComponent } from '../../lib/ui/ui-badge.component';
import { UiComboboxComponent } from '../../lib/ui/ui-combobox.component';
import { UiDataColumn, UiDataTableComponent } from '../../lib/ui/ui-data-table.component';
import { UiFieldComponent } from '../../lib/ui/ui-field.component';
import { UiPaginatorComponent } from '../../lib/ui/ui-paginator.component';
import { DemoCatalogueResource, DemoProduct, makeProducts, searchProducts } from './demo-catalogue';

const money = (value: number): string => value.toFixed(2);

/**
 * `ui-data-table` + `ui-paginator` + `ui-combobox` driven by an in-memory `BasePagedResource`
 * over 10,000 rows — the same shapes Task 10 binds to the real catalogue API.
 */
@Component({
  selector: 'app-demo-catalogue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    UiDataTableComponent,
    UiPaginatorComponent,
    UiComboboxComponent,
    UiFieldComponent,
    UiAlertComponent,
    UiBadgeComponent,
  ],
  template: `
    <h4 class="sub">ui-combobox</h4>
    <div class="picker">
      <ui-field label="Product" for="demo-product" hint="Type a code or name prefix: LOAD-0001, Widget, Bolt Gr…">
        <ui-combobox
          inputId="demo-product"
          [options]="matches()"
          [optionLabel]="productLabel"
          [optionKey]="productKey"
          [loading]="searching()"
          [data]="true"
          placeholder="Search products"
          [(selected)]="picked"
          (search)="lookup($event)"
        />
      </ui-field>
      <p class="muted">
        Selected:
        @if (picked(); as product) {
          <span class="data">{{ product.code }}</span> — {{ product.name }}
        } @else {
          none
        }
      </p>
    </div>

    <h4 class="sub">ui-data-table + ui-paginator</h4>
    <div class="toolbar">
      <ui-field label="Search" for="demo-search" hint="Prefix on code or name; debounced 300 ms.">
        <input
          id="demo-search"
          class="control control--data"
          type="search"
          autocomplete="off"
          [value]="catalogue.query().search"
          (input)="onSearch($event)"
        />
      </ui-field>
      <label class="check">
        <input type="checkbox" [checked]="catalogue.query().inStock" (change)="onInStock($event)" />
        In stock only
      </label>
    </div>

    @if (catalogue.error(); as error) {
      <ui-alert tone="stop" title="Catalogue">{{ error.message }}</ui-alert>
    }

    <ng-template #stockCell let-product>
      <ui-badge
        [status]="product.availableQuantity > 0 ? 'Confirmed' : 'InsufficientStock'"
        [label]="product.availableQuantity > 0 ? product.availableQuantity + ' in stock' : 'Sold out'"
      />
    </ng-template>

    <ui-data-table
      caption="Catalogue"
      [columns]="columns()"
      [rows]="catalogue.items()"
      [sort]="sort()"
      [loading]="catalogue.loading()"
      [rowKey]="productKey"
      (sortChange)="onSort($event)"
    >
      <p slot="empty" class="muted">No products match. Clear the search or untick “In stock only”.</p>
    </ui-data-table>

    <ui-paginator
      class="paginator"
      [pageIndex]="catalogue.pageIndex()"
      [count]="catalogue.items().length"
      [pageSize]="catalogue.query().pageSize"
      [hasPrev]="catalogue.hasPrev()"
      [hasMore]="catalogue.hasMore()"
      [loading]="catalogue.loading()"
      [total]="catalogue.total()"
      (previous)="catalogue.prev()"
      (next)="catalogue.next()"
      (pageSizeChange)="catalogue.setQuery({ pageSize: $event })"
    />
  `,
  styles: `
    .sub {
      margin: var(--s-6) 0 var(--s-3);
      font-size: var(--t-sm);
      font-weight: 600;
    }

    .picker {
      max-width: 380px;
    }

    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      gap: var(--s-4);
    }

    .toolbar ui-field {
      flex: 1 1 240px;
      max-width: 380px;
    }

    .check {
      display: flex;
      align-items: center;
      gap: var(--s-2);
      margin-bottom: var(--s-4);
      padding-bottom: 1.25em;
      font-size: var(--t-sm);
    }

    ui-alert {
      margin-bottom: var(--s-3);
    }

    .paginator {
      margin-top: var(--s-3);
    }

    .muted {
      color: var(--c-ink-muted);
    }

    .data {
      font-family: var(--f-data);
    }
  `,
})
export class DemoCatalogueComponent {
  private readonly products = makeProducts(10_000);

  protected readonly catalogue = new DemoCatalogueResource(this.products);

  private readonly stockCell = viewChild<TemplateRef<{ $implicit: DemoProduct }>>('stockCell');

  protected readonly columns = computed<readonly UiDataColumn<DemoProduct>[]>(() => [
    { field: 'code', header: 'Code', data: true, sortable: true, cell: (p) => p.code },
    { field: 'name', header: 'Name', sortable: true, cell: (p) => p.name },
    { field: 'price', header: 'Price', numeric: true, sortable: true, cell: (p) => money(p.price) },
    {
      field: 'stock',
      header: 'Stock',
      numeric: true,
      cell: (p) => String(p.availableQuantity),
      template: this.stockCell(),
    },
  ]);

  protected readonly sort = computed(() => parseSort(this.catalogue.query().sort ?? 'code'));

  protected readonly productLabel = (p: DemoProduct): string => `${p.code}  ${p.name}`;
  protected readonly productKey = (p: DemoProduct): string => p.code;

  protected readonly picked = signal<DemoProduct | null>(null);
  protected readonly matches = signal<readonly DemoProduct[]>([]);
  protected readonly searching = signal(false);

  private readonly lookups = new Subject<string>();

  constructor() {
    this.lookups
      .pipe(
        tap(() => this.searching.set(true)),
        switchMap((text) => searchProducts(this.products, text)),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((found) => {
        this.matches.set(found);
        this.searching.set(false);
      });

    this.catalogue.reload();
  }

  protected lookup(text: string): void {
    this.lookups.next(text);
  }

  protected onSearch(event: Event): void {
    this.catalogue.setQuery({ search: (event.target as HTMLInputElement).value });
  }

  protected onInStock(event: Event): void {
    this.catalogue.setQuery({ inStock: (event.target as HTMLInputElement).checked });
  }

  protected onSort(sort: Sort): void {
    this.catalogue.setQuery({ sort: sortParam(sort) });
  }
}
