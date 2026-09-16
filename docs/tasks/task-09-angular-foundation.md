# Task 9 — Angular foundation: design system and `lib/`

Read `docs/design-system.md` and build to it. Keep the architecture flat: **forms**, **views**,
and a `lib/` folder of reusable base code. No NgRx, no facades, no barrel-file mazes.

## Structure

```
web/order-console/src/app/
  core/
    http/api.interceptor.ts        base url, correlation id, error -> ApiError mapping
    config/app.config.ts           environment + apiBaseUrl
  lib/
    api/base-api.service.ts        get/post<T>, unwraps data, throws ApiError
    api/api-error.ts              code, message, status, extensions, retryAfterSeconds (from Retry-After)
    api/base-event-stream.ts       EventSource wrapper as a signal: data/connected/error, reconnects, stops on `done`
    forms/base-form.component.ts   loading/error/submitted signals, markAllAsTouched, errorFor()
    state/base-resource.ts         signal wrapper: data/loading/error + reload()
    state/base-paged-resource.ts   server-side paging: items/loading/error/hasMore/total/pageIndex,
                                   setQuery() (debounced), next()/prev() over a cursor stack, reload()
    api/paging.ts                  Page<T> and PagedQuery mirrors of the API (Task 15)
    ui/                            ui-button, ui-field, ui-table, ui-badge, ui-status-band,
                                   ui-alert, ui-spinner  (standalone, presentational)
                                   ui-data-table (server-side sort headers), ui-paginator,
                                   ui-combobox (typeahead)  — see "Paged primitives"
    tokens/_tokens.scss            the token block from docs/design-system.md
    util/idempotency-key.ts        newKey() using crypto.randomUUID()  (client key; unrelated to server Snowflake ids)
  features/orders/                 (Task 10)
```

## `BaseApiService`

```ts
export abstract class BaseApiService {
  protected readonly http = inject(HttpClient);
  protected readonly base = inject(APP_CONFIG).apiBaseUrl;
  protected get<T>(path: string): Observable<T>
  protected post<T>(path: string, body?: unknown, headers?: Record<string,string>): Observable<T>
}
```

Feature services extend it and only declare routes. HTTP error → `ApiError` happens once, in the
interceptor, by reading ProblemDetails `code` + `extensions` and the `Retry-After` header. No
component ever sees an `HttpErrorResponse`. `ApiError.isRetryableWithSameKey` is true for
network errors, timeouts, and every `503` (`stock.busy`, `server.busy`, `server.timeout`) plus
`409 idempotency.in_progress` — Task 10's retry button keys off that one flag.

## `BaseEventStream<T>`

```ts
export abstract class BaseEventStream<T> {
  readonly data = signal<T | null>(null);
  readonly connected = signal(false);
  readonly error = signal<ApiError | null>(null);
  protected open(path: string, event: string): void   // new EventSource(base + path); parses `data` JSON as T on `event`
  close(): void
}
```

Reconnects are the browser's (`EventSource` does it); a `done` event closes for good; a `503`
on connect surfaces as `ApiError` with `sse.full` and the component falls back to a slow poll
through `BaseResource.reload()` every 10 s. Nothing else in the app may construct an
`EventSource`.

## `BaseFormComponent`

Holds what every form repeats: `submitting`, `serverError`, `submitted` signals, a
`errorFor(control)` helper returning the message string, and a `submit()` template method that
guards double submission. Subclasses define the `FormGroup` and `perform()`. This is the DRY seam
— if `order-create-form` re-implements a loading flag, the base class is wrong.

## UI components

Each `ui-*` is standalone, takes inputs, emits outputs, has zero HTTP and zero router knowledge.
`ui-status-band` and `ui-badge` own the single status→colour+label map from the design system.

Accessibility floor from the design doc is part of this task, not a later polish pass:
visible focus ring, `aria-live` error region in `ui-field`, `prefers-reduced-motion` respected,
colour never the only signal.

## Paged primitives

The catalogue can be large (Task 15 pages it on the server), so the list machinery is base code,
not feature code. No CDK, no grid library — three small components and one resource.

**`BasePagedResource<TItem, TQuery>`** (`lib/state/`). Wraps `GET` with `Page<T>`:
`items`, `loading`, `error`, `hasMore`, `total` (first page only, kept while the query is
unchanged), `pageIndex`. `setQuery(patch)` resets the cursor stack and, for `search`, debounces
300 ms; `next()` pushes the current cursor and requests `nextCursor`; `prev()` pops; `reload()`
re-requests the current page with the same cursor. Every call cancels the in-flight request
(`switchMap`/`AbortSignal`) so a fast typist never sees an older page land after a newer one.

**`ui-data-table`** — column defs (`field`, `header`, `sortable`, cell template), `sort` input
and `sortChange` output (`{ field, dir }`), `aria-sort` on the active header, sticky header,
loading overlay that keeps the previous rows visible, empty state slot, and a card layout under
600 px. It renders what it is given; it never sorts or filters client-side.

**`ui-paginator`** — Previous/Next (disabled at the ends and while loading), page size select
(25/50/100/200), `Page 3 · 50 rows` and `of 12,340` when `total` is known, and a polite
`aria-live` announcement on page change.

**`ui-combobox`** — the WAI-ARIA 1.2 combobox pattern with a listbox popup: `role="combobox"`,
`aria-expanded`, `aria-controls`, `aria-activedescendant`, options with `role="option"`;
keyboard ↑ ↓ Home End Enter Escape; `search` output (debounced 300 ms, min 1 char), `options`
input, `optionTemplate`, `selected` two-way model, `loading` and `No matches` states. It knows
nothing about products or HTTP.

## Definition of done

- [x] `npm start` renders a shell with the two-column layout and tokens applied
- [x] A demo route shows every `ui-*` component in every state (this is the design reference),
      including `ui-data-table` + `ui-paginator` + `ui-combobox` driven by an in-memory
      `BasePagedResource` over 10,000 fake rows
- [x] No component imports `HttpClient` directly except `BaseApiService`
- [x] `ui-combobox` passes a keyboard-only pass: type, arrow, Enter selects, Escape closes, focus never lost
- [x] Lint clean, strict TypeScript, no `any`
