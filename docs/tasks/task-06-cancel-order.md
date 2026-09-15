# Task 6 — POST /api/orders/{id}/cancel

Cancellation is idempotent and restores stock exactly once, under any amount of concurrency.

## Handler — `Features/Orders/CancelOrder/CancelOrderCommand.cs`

One transaction:

1. Guarded status transition — this is the exactly-once gate:

   ```sql
   UPDATE orders
      SET status = 'Cancelled', cancelled_at = @now
    WHERE id = @id AND status = 'Confirmed';
   ```

2. Rows affected `1` → restore stock for every line, **one statement per line, ordered by
   product code** — the same lock order Task 4 uses to deduct, so a cancel racing a create on
   overlapping products cannot deadlock:

   ```sql
   SELECT product_code, quantity FROM order_lines WHERE order_id = @id ORDER BY product_code;

   -- per line
   UPDATE products SET available_quantity = available_quantity + @qty WHERE code = @code;
   ```

3. Rows affected `0` → decide with one read:
   - order exists and is already `Cancelled` → `200 OK` (idempotent, **no** stock restore)
   - order does not exist → `404` `order.not_found`

Commit. Response body = current `OrderDetailDto`.

Whoever loses the race gets branch 3 and touches nothing. The status column is the lock.

## Explicitly forbidden

- Loading the order, checking `if (order.Status == Confirmed)` in C#, then saving. That is the
  race this task exists to avoid.
- Restoring stock outside the transaction that flipped the status.
- Any in-memory "already cancelling" set.

## Notifications

Cancelling does not create a new notification and does not change the `order.created` outbox row.
If that row is still `Pending`, the worker still delivers it — document that in the README as a
known, accepted behaviour (the created event really did happen).

## Definition of done

- [ ] Integration test: two concurrent cancels of the same order → both return 200, status
      `Cancelled`, stock restored exactly once (quantity back to its pre-order value, not double)
- [ ] Cancel unknown id → 404
- [ ] Cancel, then replay the original submission with its idempotency key → 200 `Cancelled`,
      no new order, no second deduction
- [ ] Cancel is covered by a test that asserts the `UPDATE ... WHERE status='Confirmed'` guard
      actually runs (e.g. rows-affected assertion or SQL log)
