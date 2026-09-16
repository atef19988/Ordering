import { Location } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  effect,
  inject,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { DEFAULT_PAGE_SIZE, PAGE_SIZES, Sort, parseSort, sortParam } from '../../../lib/api/paging';
import { UiAlertComponent } from '../../../lib/ui/ui-alert.component';
import { UiBadgeComponent } from '../../../lib/ui/ui-badge.component';
import { UiButtonComponent } from '../../../lib/ui/ui-button.component';
import { UiDataColumn, UiDataTableComponent } from '../../../lib/ui/ui-data-table.component';
import { UiFieldComponent } from '../../../lib/ui/ui-field.component';
import { UiPaginatorComponent } from '../../../lib/ui/ui-paginator.component';
import { CatalogueQuery, CatalogueSortField, Product } from '../models/order.models';
import { CatalogueApi } from '../services/catalogue.api';
import { CatalogueResource, DEFAULT_CATALOGUE_QUERY } from '../services/catalogue.resource';

const SORT_FIELDS: ReadonlySet<string> = new Set<CatalogueSortField>(['code', 'name', 'price']);

/** The query keys the panel writes to the URL — the API's five, plus `page` so the label survives a paste. */
type UrlParams = Record<'search' | 'inStock' | 'sort' | 'pageSize' | 'cursor' | 'page', string | null>;

/** What `history.state` carries alongside the URL: the cursors behind the current page. */
interface PanelState {
  readonly trail?: readonly (string | null)[];
}

const money = (value: number): string => value.toFixed(2);
const clock = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

function queryFromParams(params: ParamMap): CatalogueQuery {
  const sort = params.get('sort') ?? '';
  const pageSize = Number(params.get('pageSize'));
  return {
    search: params.get('search') ?? '',
    inStock: params.get('inStock') === 'true',
    // Whitelisted here too, so a hand-edited link cannot leave the panel stuck on a 400.
    sort: SORT_FIELDS.has(parseSort(sort).field) ? sort : DEFAULT_CATALOGUE_QUERY.sort,
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : DEFAULT_PAGE_SIZE,
    cursor: null,
  };
}

function sameQuery(a: CatalogueQuery, b: CatalogueQuery): boolean {
  return (
    (a.search ?? '') === (b.search ?? '') &&
    a.inStock === b.inStock &&
    a.sort === b.sort &&
    a.pageSize === b.pageSize
  );
}

/**
 * The catalogue, one page at a time, over `CatalogueResource`. Search (prefix), `In stock only`,
 * sortable code / name / price headers and the page size are all sent to the server; the table
 * renders what comes back. `search`, `inStock`, `sort`, `pageSize` and `cursor` (plus `page`)
 * mirror to the route's query params and the Previous trail rides in `history.state`, so a
 * refresh, the back button or a pasted link land on the same page. A stale cursor
 * (`400 paging.invalid_cursor`) restarts silently at page 1 of the same query — the resource
 * does that. The page calls `reload()` after a create, a cancel or a stock conflict.
 */
@Component({
  selector: 'app-stock-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    UiDataTableComponent,
    UiPaginatorComponent,
    UiFieldComponent,
    UiButtonComponent,
    UiAlertComponent,
    UiBadgeComponent,
  ],
  template: `
    <div class="toolbar">
      <ui-field class="search" label="Search" for="stock-search">
        <input
          id="stock-search"
          class="control control--data"
          type="search"
          autocomplete="off"
          spellcheck="false"
          maxlength="64"
          placeholder="Code or name starts with…"
          [value]="catalogue.query().search"
          (input)="onSearch($event)"
        />
      </ui-field>
      <label class="check">
        <input type="checkbox" [checked]="catalogue.query().inStock" (change)="onInStock($event)" />
        In stock only
      </label>
      <p class="as-of muted" aria-live="polite">
        @if (catalogue.asOf(); as asOf) {
          as of <span class="data">{{ clock(asOf) }}</span> · may be ≤ 1 s behind
        }
      </p>
    </div>

    @if (catalogue.error(); as error) {
      <ui-alert tone="stop" title="Catalogue" class="alert">
        {{ error.message }}
        <ui-button slot="actions" (pressed)="catalogue.reload()">Try again</ui-button>
      </ui-alert>
    }

    <ng-template #stockCell let-product>
      @if (product.availableQuantity > 0) {
        <span class="data">{{ product.availableQuantity }}</span>
      } @else {
        <ui-badge status="InsufficientStock" label="Sold out" />
      }
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
      <div slot="empty" class="empty">
        <p class="muted">{{ emptyMessage() }}</p>
        @if (canClear()) {
          <ui-button (pressed)="clear()">Clear</ui-button>
        }
      </div>
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
    :host {
      display: block;
      margin-top: var(--s-3);
      --table-max-height: 50vh;
    }

    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      gap: var(--s-2) var(--s-4);
    }

    .search {
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

    .as-of {
      margin-left: auto;
      margin-bottom: var(--s-4);
      padding-bottom: 1.25em;
      font-size: var(--t-sm);
      min-height: 1.25em;
    }

    .alert {
      display: block;
      margin-bottom: var(--s-3);
    }

    .empty {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--s-3);
    }

    .paginator {
      margin-top: var(--s-3);
    }

    .muted {
      color: var(--c-ink-muted);
    }

    @media (max-width: 599px) {
      .as-of {
        margin-left: 0;
      }
    }
  `,
})
export class StockPanelView {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly location = inject(Location);

  protected readonly catalogue = new CatalogueResource(inject(CatalogueApi));

  private readonly stockCell = viewChild<TemplateRef<{ $implicit: Product }>>('stockCell');

  protected readonly columns = computed<readonly UiDataColumn<Product>[]>(() => [
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
  protected readonly productKey = (product: Product): string => product.code;

  protected readonly canClear = computed(() => {
    const query = this.catalogue.query();
    return (query.search ?? '') !== '' || query.inStock;
  });

  protected readonly emptyMessage = computed(() => {
    const { search, inStock } = this.catalogue.query();
    if (search) {
      return `No products start with "${search}".`;
    }
    return inStock ? 'Nothing is in stock.' : 'No products.';
  });

  private lastMirrored: UrlParams | null = null;

  constructor() {
    // URL → panel: the first paint, the back button, a pasted link.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed())
      .subscribe((params) => this.syncFromUrl(params));

    // Panel → URL, so the position survives a refresh and back/forward walks the pages.
    effect(() => {
      const query = this.catalogue.query();
      const cursor = this.catalogue.cursor();
      const pageIndex = this.catalogue.pageIndex();
      const trail = this.catalogue.trail();
      untracked(() => this.mirrorToUrl(query, cursor, pageIndex, trail));
    });
  }

  reload(): void {
    this.catalogue.reload();
  }

  protected clock(at: Date): string {
    return clock.format(at);
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

  protected clear(): void {
    this.catalogue.setQuery({ search: '', inStock: false });
  }

  private syncFromUrl(params: ParamMap): void {
    const query = queryFromParams(params);
    const cursor = params.get('cursor');
    const alreadyThere =
      this.lastMirrored !== null &&
      sameQuery(query, this.catalogue.query()) &&
      cursor === this.catalogue.cursor();
    if (alreadyThere) {
      return; // our own mirror coming back
    }
    const page = Number(params.get('page'));
    const state = this.location.getState() as PanelState | null;
    this.catalogue.restore(
      query,
      cursor,
      Number.isInteger(page) && page > 1 ? page - 1 : 0,
      state?.trail ?? [],
    );
  }

  private mirrorToUrl(
    query: CatalogueQuery,
    cursor: string | null,
    pageIndex: number,
    trail: readonly (string | null)[],
  ): void {
    const params: UrlParams = {
      search: query.search || null,
      inStock: query.inStock ? 'true' : null,
      sort: query.sort === DEFAULT_CATALOGUE_QUERY.sort ? null : (query.sort ?? null),
      pageSize: query.pageSize === DEFAULT_PAGE_SIZE ? null : String(query.pageSize),
      cursor,
      page: pageIndex > 0 ? String(pageIndex + 1) : null,
    };

    const previous = this.lastMirrored;
    const onlySearchChanged =
      previous !== null &&
      (Object.keys(params) as (keyof UrlParams)[]).every(
        (key) => key === 'search' || params[key] === previous[key],
      );
    this.lastMirrored = params;

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: params,
      // Typing rewrites the entry in place; a page, sort or filter change is a step the back button undoes.
      replaceUrl: previous === null || onlySearchChanged,
      state: { trail } satisfies PanelState,
    });
  }
}
