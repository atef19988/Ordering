# Design system — Order console

## Brief

An internal console used by warehouse operations staff. They submit orders, look one up by id,
cancel it, and check whether the customer notification went out. They scan more than they read,
they compare identifiers character by character, and they act on status. The design job is
**legibility of state under time pressure**, not marketing.

## Direction

Instrument panel, not landing page. Dense, quiet, left-aligned, no cards-in-a-grid, no gradients,
no decorative shadows. One bold element: the status band on the order detail, which is the only
saturated colour on the screen.

## Tokens — `lib/tokens/_tokens.scss`

```scss
:root {
  /* base */
  --c-ink:        #0F2434;  /* navy text, not tinted black */
  --c-ink-muted:  #5A6B7A;
  --c-line:       #D3DBE2;
  --c-surface:    #FFFFFF;
  --c-panel:      #F1F4F7;  /* cool paper for the action column */

  /* state — these carry meaning, so they are the only saturated colours */
  --c-ok:         #16624A;  /* confirmed, sent */
  --c-wait:       #8A5A00;  /* pending delivery, retrying */
  --c-stop:       #97242A;  /* stock conflict, failed delivery */
  --c-void:       #6B7683;  /* cancelled */

  /* type */
  --f-ui:   "IBM Plex Sans", system-ui, sans-serif;
  --f-data: "IBM Plex Mono", ui-monospace, monospace;

  --t-xs: 0.75rem; --t-sm: 0.875rem; --t-base: 1rem;
  --t-lg: 1.25rem; --t-xl: 1.75rem; --t-2xl: 2.25rem;

  /* space — 4px base */
  --s-1: 4px; --s-2: 8px; --s-3: 12px; --s-4: 16px;
  --s-6: 24px; --s-8: 32px; --s-12: 48px;

  --r-sm: 3px; --r-md: 5px;   /* small radii; this is an instrument, not a toy */
}
```

**Monospace is load-bearing, not decoration.** Order ids, idempotency keys, product codes,
quantities and money use `--f-data` so digits align in columns and two ids can be compared by
eye. Everything else uses `--f-ui`.

## Layout

```
┌──────────────────────────────────────────────────────────────┐
│ Order console                                    db: healthy │
├────────────────────────┬─────────────────────────────────────┤
│  NEW ORDER  (panel)    │  ORDER 8f31…                        │
│  customer reference    │  ████ Confirmed   notified: Pending │
│  ┌ line ─────────────┐ │  ─────────────────────────────────── │
│  │ code   qty   ✕    │ │  code      qty    unit      amount  │
│  └───────────────────┘ │  SKU-001     2    12.50      25.00  │
│  + add line            │  SKU-002     1     4.00       4.00  │
│  key: 2f9c…  [new key] │  ─────────────────────────────────── │
│  [ Place order ]       │  total                      29.00   │
│                        │  [ Cancel order ]                   │
├────────────────────────┴─────────────────────────────────────┤
│  STOCK   SKU-001  12.50   qty 9   │  SKU-002  4.00   qty 40   │
└──────────────────────────────────────────────────────────────┘
```

Two columns at ≥1024px (action 380px fixed, detail fluid), stacked below. Content left-aligned,
numbers right-aligned in tables. Max line length 70ch for any prose.

## Components — `lib/ui/`

`ui-button`, `ui-field` (label + control + error), `ui-table`, `ui-status-band`, `ui-badge`,
`ui-alert`, `ui-spinner`. Each is a standalone component, presentational only, no HTTP.

Status colour mapping is defined once in `ui-badge`/`ui-status-band`:

| State | Colour | Label |
|---|---|---|
| Confirmed / Sent | `--c-ok` | Confirmed / Notified |
| Pending / Retrying | `--c-wait` | Notification pending |
| Insufficient stock / Failed | `--c-stop` | Not enough stock / Notification failed |
| Cancelled | `--c-void` | Cancelled |

Never signal state with colour alone — every state also has a word.

## Copy rules

Active voice, sentence case, say what happened and what to do:

- `Place order` → toast `Order placed`
- `Cancel order` → confirm `Cancel this order? Stock goes back to stock.` → toast `Order cancelled`
- Stock conflict: `Only 1 left of SKU-001. Lower the quantity or pick another product.`
- Duplicate in progress: `This order is still being processed. Retry with the same key.`
- Key reuse: `That key was used for a different order. Start a new order to get a new key.`
- Delivery failed: `Customer notification failed after 5 attempts.`
- Empty detail: `Look up an order by id, or place one.`

## Quality floor

Keyboard focus visible (2px `--c-ink` outline), `prefers-reduced-motion` respected, all state
text ≥4.5:1 contrast, works down to 360px, form errors announced via `aria-live="polite"`.
Motion only answers an action: the status band cross-fades when status changes. Nothing else moves.
