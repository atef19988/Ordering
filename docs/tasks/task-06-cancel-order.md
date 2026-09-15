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

   `IOrderRepository.TryCancelAsync(id, now, ct)` returns rows affected. Fill the
   `// Task 6:` seam Task 4 left in `IOrderRepository`.

2. Rows affected `1` → restore stock for every line, **one statement per line, ordered by
   product code** — the same lock order Task 4 uses to deduct, so a cancel racing a create on
   overlapping products cannot deadlock:

   ```sql
   SELECT product_code, quantity FROM order_lines WHERE order_id = @id ORDER BY product_code;

   -- per line: IStockRepository.RestoreAsync(code, qty, ct)
   UPDATE products SET available_quantity = available_quantity + @qty WHERE code = @code;
   ```

   `RestoreAsync` must affect exactly one row; zero means the catalogue lost a product that has
   order lines, which the FK forbids — throw, do not swallow.

3. Rows affected `0` → decide with one read (the Task 3 query is fine):
   - order exists and is already `Cancelled` → `200 OK` (idempotent, **no** stock restore)
   - order does not exist → `404` `order.not_found`

Commit. Response body = current `OrderDetailDto`, built the same way Task 4 builds the 201 body
when the handler did the cancelling (in memory, from the rows it just wrote), and from the Task 3
query when it did not.

Whoever loses the race gets branch 3 and touches nothing. The status column is the lock.

## Lock window

The restore updates run **after** the status flip and are the last statements before commit.
`SET LOCK_TIMEOUT 3000` from Task 5 applies here too (call `SetLockTimeoutAsync` first); a
timeout on a product row maps to `503 stock.busy` exactly as in create, and the status flip rolls
back with it — nothing is half cancelled.

## Seams for later tasks

Leave these as `// Task N:` comments; do not implement them here.

- `// Task 13:` after commit, publish a change hint for the order id (`IUnitOfWork.OnCommitted`).
- `// Task 14:` after commit, release the restored quantities in the Redis stock gate and evict
  the catalogue cache.

Nothing runs after the commit in this task — the handler returns and the endpoint maps the
result. No I/O outside the transaction, no I/O to anything but SQL Server inside it.

## Explicitly forbidden

- Loading the order, checking `if (order.Status == Confirmed)` in C#, then saving. That is the
  race this task exists to avoid.
- Restoring stock outside the transaction that flipped the status.
- Any in-memory "already cancelling" set.

## Notifications

Cancelling does not create a new notification and does not change the `order.created` outbox row.
If that row is still `Pending`, the relay still publishes it and the consumer still delivers it —
document that in the README as a known, accepted behaviour (the created event really did happen).

## Definition of done

- [ ] Integration test: two concurrent cancels of the same order → both return 200, status
      `Cancelled`, stock restored exactly once (quantity back to its pre-order value, not double)
- [ ] Cancel unknown id → 404
- [ ] Cancel, then replay the original submission with its idempotency key → 200 `Cancelled`,
      no new order, no second deduction
- [ ] Cancel is covered by a test that asserts the `UPDATE ... WHERE status='Confirmed'` guard
      actually runs (e.g. rows-affected assertion or SQL log)
- [ ] Create and cancel racing on two overlapping products (A+B vs B+A) 50 times → no deadlock
      victim (error 1205) in the logs, stock adds up at the end
