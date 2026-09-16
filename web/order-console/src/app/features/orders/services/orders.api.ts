import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from '../../../lib/api/base-api.service';
import { CreateOrderRequest, OrderDetail } from '../models/order.models';

/** The three order routes. Failures arrive as `ApiError`; the interceptor has already mapped them. */
@Injectable({ providedIn: 'root' })
export class OrdersApi extends BaseApiService {
  getById(id: string): Observable<OrderDetail> {
    return this.get<OrderDetail>(`/orders/${encodeURIComponent(id)}`);
  }

  /** `201` with the new order, or `200` with the stored one when the key is a replay. */
  create(request: CreateOrderRequest, idempotencyKey: string): Observable<OrderDetail> {
    return this.post<OrderDetail>('/orders', request, { 'Idempotency-Key': idempotencyKey });
  }

  /** Idempotent on the server: an already cancelled order answers `200` with its current state. */
  cancel(id: string): Observable<OrderDetail> {
    return this.post<OrderDetail>(`/orders/${encodeURIComponent(id)}/cancel`);
  }
}
