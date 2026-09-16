import { PagedQuery } from '../../../lib/api/paging';

/**
 * Mirrors of the API DTOs. Ids are 64-bit Snowflakes serialised as JSON strings — they stay
 * `string` here and are never parsed to `number` (2^53 is smaller than a Snowflake). Money is a
 * JSON number with two decimals; it is only ever displayed, never summed on the client.
 */

export type OrderStatus = 'Confirmed' | 'Cancelled';
export type NotificationStatus = 'Pending' | 'Sent' | 'Failed' | 'None';

export interface OrderLine {
  readonly productCode: string;
  readonly quantity: number;
  readonly unitPrice: number;
  readonly lineTotal: number;
}

/** `OrderDetailDto` — what `GET /api/orders/{id}`, both `POST`s and every stream frame carry. */
export interface OrderDetail {
  readonly id: string;
  readonly customerReference: string;
  readonly status: OrderStatus | string;
  /** Computed on the server from catalogue prices; the form never sends a price. */
  readonly total: number;
  readonly createdAt: string;
  readonly cancelledAt: string | null;
  readonly notificationStatus: NotificationStatus | string;
  readonly notificationAttempts: number;
  readonly lines: readonly OrderLine[];
}

/** `CreateOrderRequest` — the body of `POST /api/orders`. The key travels in the header. */
export interface CreateOrderRequest {
  readonly customerReference: string;
  readonly lines: readonly { readonly productCode: string; readonly quantity: number }[];
}

/** `ProductDto` — one row of the paged catalogue. */
export interface Product {
  readonly code: string;
  readonly name: string;
  readonly price: number;
  readonly availableQuantity: number;
}

export type CatalogueSortField = 'code' | 'name' | 'price';

/** The five query keys of `GET /api/products` (Task 15); the same five are mirrored to the URL. */
export interface CatalogueQuery extends PagedQuery {
  readonly inStock: boolean;
}

/** Nothing can change on an order once it is cancelled and its notification has a verdict. */
export function isFinal(order: OrderDetail): boolean {
  return (
    order.status === 'Cancelled' &&
    (order.notificationStatus === 'Sent' || order.notificationStatus === 'Failed')
  );
}

/** A Snowflake id on the wire: digits only, and nineteen of them in practice. */
export const ORDER_ID_PATTERN = /^\d{15,20}$/;
