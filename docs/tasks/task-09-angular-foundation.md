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
    api/api-error.ts              code, message, status, extensions
    forms/base-form.component.ts   loading/error/submitted signals, markAllAsTouched, errorFor()
    state/base-resource.ts         signal wrapper: data/loading/error + reload()
    ui/                            ui-button, ui-field, ui-table, ui-badge, ui-status-band,
                                   ui-alert, ui-spinner  (standalone, presentational)
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
interceptor, by reading ProblemDetails `code` + `extensions`. No component ever sees an
`HttpErrorResponse`.

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

## Definition of done

- [ ] `npm start` renders a shell with the two-column layout and tokens applied
- [ ] A demo route shows every `ui-*` component in every state (this is the design reference)
- [ ] No component imports `HttpClient` directly except `BaseApiService`
- [ ] Lint clean, strict TypeScript, no `any`
