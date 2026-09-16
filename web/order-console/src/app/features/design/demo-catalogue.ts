import { Observable, delay, of, throwError } from 'rxjs';
import { ApiError } from '../../lib/api/api-error';
import { DEFAULT_PAGE_SIZE, Page, PagedQuery, parseSort } from '../../lib/api/paging';
import { BasePagedResource } from '../../lib/state/base-paged-resource';

/** Mirrors Task 15's `ProductDto`. `price` is a JSON number on the wire. */
export interface DemoProduct {
  readonly code: string;
  readonly name: string;
  readonly price: number;
  readonly availableQuantity: number;
}

export interface DemoCatalogueQuery extends PagedQuery {
  readonly inStock: boolean;
}

export type DemoSortField = 'code' | 'name' | 'price';

const WORDS = ['Widget', 'Bracket', 'Gasket', 'Sprocket', 'Flange', 'Bolt', 'Washer', 'Valve'];
const COLOURS = ['Blue', 'Red', 'Green', 'Grey', 'Black', 'White'];

/** 10,000 rows with ties and shared prefixes, like the `seed --products N` catalogue. */
export function makeProducts(count: number): readonly DemoProduct[] {
  const products: DemoProduct[] = [];
  for (let i = 1; i <= count; i++) {
    const code = `LOAD-${String(i).padStart(6, '0')}`;
    const name = `${WORDS[i % WORDS.length]} ${COLOURS[(i * 7) % COLOURS.length]}`;
    const price = Number(((i * 37) % 9000) / 100 + 0.5).toFixed(2);
    products.push({ code, name, price: Number(price), availableQuantity: i % 13 === 0 ? 0 : 1000 });
  }
  return products;
}

interface CursorDocument {
  readonly v: 1;
  readonly f: DemoSortField;
  readonly d: 'asc' | 'desc';
  readonly k: string;
  readonly c: string;
  readonly h: string;
}

const encodeCursor = (doc: CursorDocument): string => btoa(JSON.stringify(doc));

function decodeCursor(cursor: string): CursorDocument {
  try {
    return JSON.parse(atob(cursor)) as CursorDocument;
  } catch {
    throw new ApiError('paging.invalid_cursor', 'The paging cursor is not valid.', 400);
  }
}

const sortKey = (product: DemoProduct, field: DemoSortField): string =>
  field === 'price' ? product.price.toFixed(2).padStart(12, '0') : product[field];

/**
 * A `BasePagedResource` over memory: the same prefix filter, whitelisted sort, keyset cursor and
 * first-page-only `total` as the API, with a short delay so the loading overlay is visible.
 * It exists so the design reference can exercise the paged primitives without a server.
 */
export class DemoCatalogueResource extends BasePagedResource<DemoProduct, DemoCatalogueQuery> {
  constructor(
    private readonly products: readonly DemoProduct[],
    private readonly latencyMs = 250,
  ) {
    super({ search: '', inStock: false, sort: 'code', pageSize: DEFAULT_PAGE_SIZE, cursor: null });
  }

  protected fetch(query: DemoCatalogueQuery): Observable<Page<DemoProduct>> {
    const sort = parseSort<DemoSortField>(query.sort ?? 'code');
    const prefix = (query.search ?? '').trim().toLowerCase();
    const filterHash = `${prefix}|${query.inStock}`;

    let after: CursorDocument | null = null;
    if (query.cursor) {
      after = decodeCursor(query.cursor);
      if (after.f !== sort.field || after.d !== sort.dir || after.h !== filterHash) {
        return throwError(
          () => new ApiError('paging.invalid_cursor', 'The paging cursor belongs to another query.', 400),
        ).pipe(delay(this.latencyMs));
      }
    }

    const direction = sort.dir === 'desc' ? -1 : 1;
    const compare = (a: DemoProduct, b: DemoProduct): number =>
      direction * (sortKey(a, sort.field).localeCompare(sortKey(b, sort.field)) || a.code.localeCompare(b.code));

    const filtered = this.products
      .filter(
        (p) =>
          (!prefix || p.code.toLowerCase().startsWith(prefix) || p.name.toLowerCase().startsWith(prefix)) &&
          (!query.inStock || p.availableQuantity > 0),
      )
      .sort(compare);

    // The seek: everything strictly after the last row the caller saw, in this sort order.
    const seek = after;
    const remaining = seek
      ? filtered.filter((p) => compareKeys(p, seek, sort.field, direction) > 0)
      : filtered;

    const items = remaining.slice(0, query.pageSize + 1);
    const hasMore = items.length > query.pageSize;
    const pageItems = hasMore ? items.slice(0, query.pageSize) : items;
    const last = pageItems.at(-1);

    return of({
      items: pageItems,
      pageSize: query.pageSize,
      hasMore,
      nextCursor:
        hasMore && last
          ? encodeCursor({ v: 1, f: sort.field, d: sort.dir, k: sortKey(last, sort.field), c: last.code, h: filterHash })
          : null,
      total: query.cursor ? null : String(filtered.length),
    }).pipe(delay(this.latencyMs));
  }
}

/** `(sortKey, code) > (k, c)` in the requested direction — the expanded row-value comparison. */
function compareKeys(product: DemoProduct, after: CursorDocument, field: DemoSortField, direction: number): number {
  const byKey = direction * sortKey(product, field).localeCompare(after.k);
  return byKey !== 0 ? byKey : direction * product.code.localeCompare(after.c);
}

/** The typeahead's data source: the first ten prefix matches, after a short delay. */
export function searchProducts(products: readonly DemoProduct[], text: string, latencyMs = 200): Observable<readonly DemoProduct[]> {
  const prefix = text.trim().toLowerCase();
  const matches = products
    .filter((p) => p.code.toLowerCase().startsWith(prefix) || p.name.toLowerCase().startsWith(prefix))
    .slice(0, 10);
  return of(matches).pipe(delay(latencyMs));
}
