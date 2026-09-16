import { DestroyRef, Signal, WritableSignal, computed, inject, signal } from '@angular/core';
import { Observable, Subject, catchError, map, of, switchMap, tap } from 'rxjs';
import { ApiError } from '../api/api-error';
import { Page, PagedQuery, totalOf } from '../api/paging';

type Outcome<TItem> =
  | { readonly cursor: string | null; readonly page: Page<TItem> }
  | { readonly cursor: string | null; readonly error: ApiError };

/**
 * Server-side paging as signals over a keyset cursor. `setQuery()` starts over on page 1 (and
 * debounces `search` so a fast typist sends one request, not one per key); `next()` pushes the
 * current cursor and asks for `nextCursor`; `prev()` pops; `reload()` re-requests the same page.
 * Every call cancels the request in flight (`switchMap`), so an older page can never land after
 * a newer one. `total` arrives on the first page only and is kept while the query is unchanged.
 * A stale cursor (`400 paging.invalid_cursor`) silently restarts at page 1 of the same query.
 * `restore()` re-enters a position that was mirrored elsewhere (Task 10 keeps `query`, `cursor`
 * and `trail` in the URL and `history.state`) so a refresh lands on the same page.
 *
 * Subclasses (Task 10's `CatalogueResource`, the in-memory demo) implement `fetch()`; nothing
 * else. Must be created in an injection context: it stops on the owner's `DestroyRef`.
 */
export abstract class BasePagedResource<TItem, TQuery extends PagedQuery> {
  readonly items = signal<readonly TItem[]>([]);
  readonly loading = signal(false);
  readonly error = signal<ApiError | null>(null);
  readonly hasMore = signal(false);
  readonly total = signal<number | null>(null);
  /** Zero-based; the paginator shows `Page pageIndex + 1`. */
  readonly pageIndex = signal(0);
  /** Anything behind a cursor counts as a previous page, even when the trail was lost (`prev()` restarts at page 1). */
  readonly hasPrev = computed(() => this.pageIndex() > 0 || this.cursor() !== null);
  /** The cursor of the page currently shown (`null` = first page). Task 10 mirrors it to the URL. */
  readonly cursor = signal<string | null>(null);
  /** The cursors behind the current page, oldest first — what `prev()` pops. Mirrored so a refresh keeps Previous working. */
  readonly trail: Signal<readonly (string | null)[]>;

  private readonly currentQuery: WritableSignal<TQuery>;
  readonly query: Signal<TQuery>;

  private nextCursor: string | null = null;
  private readonly previousCursors = signal<readonly (string | null)[]>([]);
  private readonly requests = new Subject<{ query: TQuery; cursor: string | null }>();
  private debounceHandle: ReturnType<typeof setTimeout> | null = null;

  protected constructor(
    initial: TQuery,
    private readonly searchDebounceMs = 300,
  ) {
    this.currentQuery = signal<TQuery>(initial);
    this.query = this.currentQuery.asReadonly();
    this.trail = this.previousCursors.asReadonly();

    const subscription = this.requests
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.error.set(null);
        }),
        switchMap(({ query, cursor }) =>
          this.fetch({ ...query, cursor }).pipe(
            map((page): Outcome<TItem> => ({ cursor, page })),
            catchError((error: unknown) => of<Outcome<TItem>>({ cursor, error: ApiError.from(error) })),
          ),
        ),
      )
      .subscribe((outcome) => this.apply(outcome));

    inject(DestroyRef).onDestroy(() => {
      subscription.unsubscribe();
      this.cancelDebounce();
    });
  }

  /** One page from the server (or, in a demo, from memory). The `cursor` is already on the query. */
  protected abstract fetch(query: TQuery): Observable<Page<TItem>>;

  /** Changes the filter/sort/page size and starts over on page 1. `search` is debounced. */
  setQuery(patch: Partial<TQuery>): void {
    this.currentQuery.update((query) => ({ ...query, ...patch }));
    this.resetPosition();
    if ('search' in patch) {
      this.cancelDebounce();
      this.debounceHandle = setTimeout(() => this.request(), this.searchDebounceMs);
    } else {
      this.request();
    }
  }

  /** Re-requests the page currently shown, same cursor — after a create or cancel, for instance. */
  reload(): void {
    this.request();
  }

  next(): void {
    if (!this.hasMore() || this.nextCursor === null) {
      return;
    }
    this.previousCursors.update((trail) => [...trail, this.cursor()]);
    this.cursor.set(this.nextCursor);
    this.pageIndex.set(this.previousCursors().length);
    this.request();
  }

  /** Back one page; with no trail behind a deep cursor (a pasted link) it restarts at page 1. */
  prev(): void {
    const trail = this.previousCursors();
    if (trail.length === 0) {
      if (this.cursor() !== null) {
        this.resetPosition();
        this.request();
      }
      return;
    }
    this.cursor.set(trail[trail.length - 1]);
    this.previousCursors.set(trail.slice(0, -1));
    this.pageIndex.set(trail.length - 1);
    this.request();
  }

  /**
   * Jumps straight to a mirrored position — query, cursor, page number and the trail behind it —
   * and requests that page once, without debouncing. `total` is unknown off the first page and
   * stays so until the query changes.
   */
  restore(
    query: TQuery,
    cursor: string | null,
    pageIndex: number,
    trail: readonly (string | null)[],
  ): void {
    this.currentQuery.set(query);
    this.resetPosition();
    this.cursor.set(cursor);
    this.previousCursors.set(cursor === null ? [] : trail);
    this.pageIndex.set(cursor === null ? 0 : Math.max(pageIndex, 0));
    this.request();
  }

  private apply(outcome: Outcome<TItem>): void {
    this.loading.set(false);

    if ('error' in outcome) {
      if (outcome.error.code === 'paging.invalid_cursor' && outcome.cursor !== null) {
        this.resetPosition();
        this.request();
        return;
      }
      // Previous rows stay visible under the error; the owner decides how loud to be.
      this.error.set(outcome.error);
      return;
    }

    const { page, cursor } = outcome;
    this.items.set(page.items);
    this.hasMore.set(page.hasMore);
    this.nextCursor = page.nextCursor;
    if (cursor === null) {
      this.total.set(totalOf(page));
    }
  }

  private resetPosition(): void {
    this.cursor.set(null);
    this.nextCursor = null;
    this.previousCursors.set([]);
    this.pageIndex.set(0);
    this.total.set(null);
  }

  private request(): void {
    this.cancelDebounce();
    this.requests.next({ query: this.currentQuery(), cursor: this.cursor() });
  }

  private cancelDebounce(): void {
    if (this.debounceHandle !== null) {
      clearTimeout(this.debounceHandle);
      this.debounceHandle = null;
    }
  }
}
