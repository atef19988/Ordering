/**
 * Mirrors of the API's paging contract (`Application/Abstractions/Paging`, Task 15). The browser
 * never holds more than one page; the server pages, filters and sorts.
 */
export interface Page<T> {
  readonly items: readonly T[];
  readonly pageSize: number;
  /** From fetching `pageSize + 1` rows, never from the count. */
  readonly hasMore: boolean;
  readonly nextCursor: string | null;
  /** Present on the first page only. A `long` on the wire, so it may arrive as a string. */
  readonly total: number | string | null;
}

export type SortDirection = 'asc' | 'desc';

export interface Sort<TField extends string = string> {
  readonly field: TField;
  readonly dir: SortDirection;
}

/** What every paged list sends. Feature queries extend it with their own filters. */
export interface PagedQuery {
  readonly search?: string;
  /** `field` or `field:desc`, as the API spells it. */
  readonly sort?: string;
  readonly pageSize: number;
  /** Opaque, from the previous page's `nextCursor`; absent = first page. */
  readonly cursor?: string | null;
}

export const PAGE_SIZES: readonly number[] = [25, 50, 100, 200];
export const DEFAULT_PAGE_SIZE = 50;

export function sortParam(sort: Sort): string {
  return sort.dir === 'desc' ? `${sort.field}:desc` : sort.field;
}

export function parseSort<TField extends string = string>(value: string): Sort<TField> {
  const [field, dir] = value.split(':');
  return { field: field as TField, dir: dir === 'desc' ? 'desc' : 'asc' };
}

/** `?a=1&b=x` from a flat object; `null`, `undefined`, `''` and `false` are left out. */
export function queryString(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === '' || value === false) {
      continue;
    }
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

export function totalOf(page: Page<unknown>): number | null {
  return page.total === null ? null : Number(page.total);
}
