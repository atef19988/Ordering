# Task 10 — Angular order feature

Forms and views only, on top of Task 9's `lib/`.

```
features/orders/
  services/orders.api.ts           extends BaseApiService
  services/order-stream.ts         extends BaseEventStream<OrderDetail>: /api/orders/{id}/events
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
- If a submission fails with anything where `ApiError.isRetryableWithSameKey` is true — network
  error, timeout, `409 idempotency.in_progress`, `503 stock.busy`, `503 server.busy`,
  `503 server.timeout` — the retry button **reuses the same key**. The UI says:
  `Retry with the same key`, and when the error carried `Retry-After` the button is disabled for
  that many seconds with a visible countdown.
- After a successful submit, or when the user clicks `New order`, generate a fresh key.
- Changing the line items does **not** rotate the key on its own — that is how the user discovers
  `409 idempotency.key_reuse`, and the UI must explain it:
  `That key was used for a different order. Start a new order to get a new key.`

Put a short comment above `idempotencyKey` explaining this policy; a reviewer will look for it.

## Live status instead of polling

The detail column opens `OrderStream` for the displayed order and renders whatever the latest
`order` event says. It never polls while the stream is connected. If the stream fails to open
(`503 sse.full`, or `EventSource` gives up), it falls back to `reload()` every 10 s and shows a
small `Live updates unavailable — refreshing every 10 s` note. On `done` the stream closes and
the view is final.

## States to render (all of them)

| State | Where | Treatment |
|---|---|---|
| Field validation | inline under field | `ui-field` error, `aria-live` |
| `409 stock.insufficient` | form-level alert | `Only {available} left of {code}. Lower the quantity or pick another product.` — when `available` is `0` say `{code} is sold out.` (the Redis gate answers with 0) + refresh stock panel |
| `409 idempotency.key_reuse` | form-level alert | copy above, offer `New order` |
| `409 idempotency.in_progress` | form-level alert | `Still processing. Retry with the same key.` + retry button |
| `503 stock.busy` | form-level alert | `High demand on {code}. Retry with the same key.` + retry button with countdown |
| `503 server.busy` / `server.timeout` | form-level alert | `The service is busy. Retry with the same key.` + retry button with countdown |
| `404 product.unknown` | inline on the line | `No product with that code` |
| Submitting | button | disabled + `ui-spinner`, no double submit |
| Notification `Pending` | detail | amber band `Notification pending` (live) |
| Notification `Sent` | detail | green `Notified` |
| Notification `Failed` | detail | red `Customer notification failed after N attempts` |
| Cancelled order | detail | grey band, cancel button hidden |
| Stream fallback | detail | the 10 s note above |
| Empty detail | detail | `Look up an order by id, or place one.` |

## Actions

- Order form: customer reference + repeatable lines (product code from a select fed by
  `GET /api/products`, quantity number ≥ 1) → `POST /api/orders`.
- Lookup: paste an order id (a 19-digit string) → `GET /api/orders/{id}` once, then the stream.
- Cancel: confirm dialog → `POST /api/orders/{id}/cancel` → the stream delivers the new state;
  refresh the stock panel.
- Stock panel: `GET /api/products`, refreshed after every successful create or cancel. The
  catalogue is served from a shared cache that may be up to 1 s behind a change made on another
  instance; the panel shows `as of {time}` so nobody mistakes it for a live count.

## Definition of done

- [ ] Place an order, see it in the detail column with a server-calculated total
- [ ] Order more than stock → the stock-conflict copy appears and stock is unchanged; sold-out copy when `available` is 0
- [ ] Retry an "uncertain" submit (kill the API mid-request) with the same key → one order only
- [ ] A `503` with `Retry-After` disables the retry button for the countdown and then retries with the same key
- [ ] Cancel updates status through the stream (no reload call in the network tab) and restores the stock panel numbers
- [ ] Notification status visibly moves `Pending -> Sent` through the stream with `FailFirstN: 2` configured; `done` closes it after a cancel
- [ ] Stream refused (`Sse:MaxConnections: 0` on the API) → the 10 s fallback works and the note shows
- [ ] Works at 360px width; keyboard-only flow completes an order
