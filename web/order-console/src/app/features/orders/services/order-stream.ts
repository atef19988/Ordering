import { BaseEventStream } from '../../../lib/api/base-event-stream';
import { OrderDetail } from '../models/order.models';

/**
 * `GET /api/orders/{id}/events` (Task 13) as signals. Every `order` frame is the full
 * `OrderDetailDto`, so the latest `data()` is always the whole truth; `done()` means the order
 * is cancelled with a notification verdict and the view is final. Created per detail view,
 * inside an injection context, so it closes with the view.
 */
export class OrderStream extends BaseEventStream<OrderDetail> {
  openFor(orderId: string): void {
    this.open(`/orders/${encodeURIComponent(orderId)}/events`, 'order');
  }
}
