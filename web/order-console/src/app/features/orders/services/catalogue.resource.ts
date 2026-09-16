import { signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { DEFAULT_PAGE_SIZE, Page } from '../../../lib/api/paging';
import { BasePagedResource } from '../../../lib/state/base-paged-resource';
import { CatalogueQuery, Product } from '../models/order.models';
import { CatalogueApi } from './catalogue.api';

export const DEFAULT_CATALOGUE_QUERY: CatalogueQuery = {
  search: '',
  inStock: false,
  sort: 'code',
  pageSize: DEFAULT_PAGE_SIZE,
  cursor: null,
};

/**
 * One page of the catalogue as signals. The API pages, filters and sorts; this class only
 * remembers where the user is. `asOf` is when the page currently shown was fetched — the API
 * serves it from a shared cache that may be up to a second behind another instance's change,
 * so the panel labels the rows with that time rather than presenting them as a live count.
 */
export class CatalogueResource extends BasePagedResource<Product, CatalogueQuery> {
  readonly asOf = signal<Date | null>(null);

  constructor(
    private readonly api: CatalogueApi,
    initial: CatalogueQuery = DEFAULT_CATALOGUE_QUERY,
  ) {
    super(initial);
  }

  protected fetch(query: CatalogueQuery): Observable<Page<Product>> {
    return this.api.getPage(query).pipe(tap(() => this.asOf.set(new Date())));
  }
}
