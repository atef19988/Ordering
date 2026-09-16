# Task 10 — Angular order feature

Forms and views only, on top of Task 9's `lib/` and Task 15's paged catalogue API.

```
features/orders/
  services/orders.api.ts           extends BaseApiService
  services/order-stream.ts         extends BaseEventStream<OrderDetail>: /api/orders/{id}/events
  services/catalogue.api.ts        extends BaseApiService: GET /api/products?search&inStock&sort&pageSize&cursor
  services/catalogue.resource.ts   extends BasePagedResource<Product, CatalogueQuery>
  models/order.models.ts           mirrors the API DTOs; order ids are `string` (64-bit Snowflake,
                                   serialised as JSON strings — never `number`, never `parseInt`)
  forms/order-create-form.component.ts
  views/order-detail.view.ts
  views/stock-panel.view.ts
  orders.page.ts                   composes the two columns
```

## The catalogue is large — never load it

"Server-side" means the API pages, filters and sorts (Task 15); the browser holds at most one
page (≤ 200 rows) at any time. No screen may request the catalogue without `pageSize`, and no
component may sort or filter product rows client-side. Angular SSR is **not** used and would not
help: the problem is data volume, not first paint.

- **Product picker** (each order line): `ui-combobox` bound to
  `GET /api/products?search={typed}&inStock=true&pageSize=20`. Options render
  `SKU-001 — Widget Blue · 10 left`; the selected value is the code. Empty input shows nothing
  (no "top 20 of everything"); `No matches` after a search. Picking sets the line's `productCode`
  control; the validator is unchanged.
- **Stock panel**: `ui-data-table` + `ui-paginator` over `CatalogueResource`: search box (prefix
  on code or name — say so in the placeholder: `Code or name starts with…`), `In stock only`
  toggle, sortable headers for code / name / price, page size select. Previous/Next only — the
  cursor design has no "jump to page"; the paginator shows `Page N` and `of total`.
- **State in the URL**: `search`, `inStock`, `sort`, `pageSize` and `cursor` are mirrored to the
  route's query params, so refresh, back button and a pasted link land on the same page.
- **After create or cancel**: `reload()` the current page (same cursor), not the first page. The
  catalogue is served from a shared cache that may be ≤ 1 s behind a change made on another
  instance; the panel shows `as of {time}` so nobody mistakes it for a live count.

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
(`503 sse.full`, `404` on an API without Task 13 yet, or `EventSource` gives up), it falls back
to `reload()` every 10 s and shows a small `Live updates unavailable — refreshing every 10 s`
note. On `done` the stream closes and the view is final.

## States to render (all of them)

| State | Where | Treatment |
|---|---|---|
| Field validation | inline under field | `ui-field` error, `aria-live` |
| `409 stock.insufficient` | form-level alert | `Only {available} left of {code}. Lower the quantity or pick another product.` — when `available` is `0` say `{code} is sold out.` (the Redis gate answers with 0) + reload the stock panel page |
| `409 idempotency.key_reuse` | form-level alert | copy above, offer `New order` |
| `409 idempotency.in_progress` | form-level alert | `Still processing. Retry with the same key.` + retry button |
| `503 stock.busy` | form-level alert | `High demand on {code}. Retry with the same key.` + retry button with countdown |
| `503 server.busy` / `server.timeout` | form-level alert | `The service is busy. Retry with the same key.` + retry button with countdown |
| `404 product.unknown` | inline on the line | `No product with that code` |
| `400 paging.invalid_cursor` | stock panel | silently reset to the first page of the same query (a stale link), no alert |
| Picker searching / no matches | combobox | spinner in the input; `No matches` option (not selectable) |
| Submitting | button | disabled + `ui-spinner`, no double submit |
| Notification `Pending` | detail | amber band `Notification pending` (live) |
| Notification `Sent` | detail | green `Notified` |
| Notification `Failed` | detail | red `Customer notification failed after N attempts` |
| Cancelled order | detail | grey band, cancel button hidden |
| Stream fallback | detail | the 10 s note above |
| Stock panel loading | panel | previous rows stay visible under the overlay; paginator disabled |
| Stock panel empty | panel | `No products start with "{search}".` with a `Clear` action |
| Empty detail | detail | `Look up an order by id, or place one.` |

## Actions

- Order form: customer reference + repeatable lines (product via the combobox, quantity number
  ≥ 1) → `POST /api/orders`.
- Lookup: paste an order id (a 19-digit string) → `GET /api/orders/{id}` once, then the stream.
- Cancel: confirm dialog → `POST /api/orders/{id}/cancel` → the stream delivers the new state;
  reload the stock panel page.
- Stock panel: search / toggle / sort / page as above.

## Definition of done

- [ ] Place an order, see it in the detail column with a server-calculated total
- [ ] With 100,000 products seeded: the stock panel's first page paints in < 1 s, typing in the picker shows results in < 300 ms, and the network tab never shows a products response with more than 200 rows
- [ ] Sort by name, go to page 3, refresh the browser → same page, same sort (URL state); Previous returns to page 2
- [ ] Order more than stock → the stock-conflict copy appears and stock is unchanged; sold-out copy when `available` is 0
- [ ] Retry an "uncertain" submit (kill the API mid-request) with the same key → one order only
- [ ] A `503` with `Retry-After` disables the retry button for the countdown and then retries with the same key
- [ ] Cancel updates status through the stream (no reload call in the network tab) and the stock panel page reloads with the restored number
- [ ] Notification status visibly moves `Pending -> Sent` through the stream with `FailFirstN: 2` configured; `done` closes it after a cancel
- [ ] Stream refused (`Sse:MaxConnections: 0` on the API) → the 10 s fallback works and the note shows
- [ ] Works at 360px width (the table becomes cards); keyboard-only flow picks a product in the combobox and completes an order
