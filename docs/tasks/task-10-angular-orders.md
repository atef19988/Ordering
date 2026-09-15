# Task 10 — Angular order feature

Forms and views only, on top of Task 9's `lib/`.

```
features/orders/
  services/orders.api.ts           extends BaseApiService
  models/order.models.ts           mirrors the API DTOs; order ids are `string` (64-bit Snowflake,
                                   serialised as JSON strings — never `number`, never `parseInt`)
  forms/order-create-form.component.ts
  views/order-detail.view.ts
  views/stock-panel.view.ts
  orders.page.ts                   composes the two columns
```

## Idempotency key handling (the part that is easy to get wrong)

- A key is generated **once** when the form becomes dirty, shown in the UI in monospace, and kept
  in a signal.
- If a submission fails with a network error, timeout, or `409 idempotency.in_progress`, the
  retry button **reuses the same key**. The UI says: `Retry with the same key`.
- After a successful submit, or when the user clicks `New order`, generate a fresh key.
- Changing the line items does **not** rotate the key on its own — that is how the user discovers
  `409 idempotency.key_reuse`, and the UI must explain it:
  `That key was used for a different order. Start a new order to get a new key.`

Put a short comment above `idempotencyKey` explaining this policy; a reviewer will look for it.

## States to render (all of them)

| State | Where | Treatment |
|---|---|---|
| Field validation | inline under field | `ui-field` error, `aria-live` |
| `409 stock.insufficient` | form-level alert | `Only {available} left of {code}. Lower the quantity or pick another product.` + refresh stock panel |
| `409 idempotency.key_reuse` | form-level alert | copy above, offer `New order` |
| `409 idempotency.in_progress` | form-level alert | `Still processing. Retry with the same key.` + retry button |
| `404 product.unknown` | inline on the line | `No product with that code` |
| Submitting | button | disabled + `ui-spinner`, no double submit |
| Notification `Pending` | detail | amber band `Notification pending`, poll every 2s, stop after 30s |
| Notification `Sent` | detail | green `Notified` |
| Notification `Failed` | detail | red `Customer notification failed after N attempts` |
| Cancelled order | detail | grey band, cancel button hidden |
| Empty detail | detail | `Look up an order by id, or place one.` |

## Actions

- Order form: customer reference + repeatable lines (product code from a select fed by
  `GET /api/products`, quantity number ≥ 1) → `POST /api/orders`.
- Lookup: paste an order id (a 19-digit string) → `GET /api/orders/{id}`.
- Cancel: confirm dialog → `POST /api/orders/{id}/cancel` → refresh detail and stock panel.
- Stock panel: `GET /api/products`, refreshed after every successful create or cancel.

## Definition of done

- [ ] Place an order, see it in the detail column with a server-calculated total
- [ ] Order more than stock → the stock-conflict copy appears and stock is unchanged
- [ ] Retry an "uncertain" submit (kill the API mid-request) with the same key → one order only
- [ ] Cancel updates status and restores the stock panel numbers
- [ ] Notification status visibly moves `Pending -> Sent` with `FailFirstN: 2` configured
- [ ] Works at 360px width; keyboard-only flow completes an order
