import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from '../../../lib/api/base-api.service';
import { Page, queryString } from '../../../lib/api/paging';
import { CatalogueQuery, Product } from '../models/order.models';

/** How many rows the typeahead asks for. Never more: the picker is for finding one product. */
export const PICKER_PAGE_SIZE = 20;

/**
 * `GET /api/products` (Task 15). Every call carries `pageSize`; there is no way to ask this
 * service for the whole catalogue. Sorting and filtering are the server's — the response is
 * rendered as it arrives.
 */
@Injectable({ providedIn: 'root' })
export class CatalogueApi extends BaseApiService {
  getPage(query: CatalogueQuery): Observable<Page<Product>> {
    return this.get<Page<Product>>(
      `/products${queryString({
        search: query.search?.trim(),
        inStock: query.inStock,
        sort: query.sort,
        pageSize: query.pageSize,
        cursor: query.cursor,
      })}`,
    );
  }

  /** The picker's data source: the first twenty in-stock products whose code or name starts with `text`. */
  search(text: string): Observable<Page<Product>> {
    return this.getPage({ search: text, inStock: true, sort: 'code', pageSize: PICKER_PAGE_SIZE });
  }
}
