# Task 15 — Catalogue at scale: server-side paging, filtering, sorting

`GET /api/products` returns the whole table today (Task 3). With two products that is right;
with a real catalogue it is the largest response in the system and the one every screen asks
for. This task makes the server page, filter and sort, and makes it impossible to ask for
everything. Nothing about the write side changes.

"Server-side" here means the API does the work and the browser never holds more than one page.
It does not mean Angular SSR — a console behind no auth has no first-paint or SEO reason for it,
and it would not shrink the data.

## Contract

```http
GET /api/products?search=SKU&inStock=true&sort=name&pageSize=50&cursor=eyJ2IjoxLC...
```

| Parameter | Type | Default | Rules |
|---|---|---|---|
| `search` | string | — | trimmed, ≤ 64 chars; **prefix** match on `code` or `name`, case-insensitive (collation) |
| `inStock` | bool | `false` | `true` → `available_quantity > 0` |
| `sort` | `code` \| `name` \| `price`, optional `:desc` | `code` | whitelist; anything else → 400 |
| `pageSize` | int | `50` | 1 … 200; outside → 400 (not clamped — an explicit error beats a silent change) |
| `cursor` | opaque string | — | from the previous response's `nextCursor`; absent = first page |

```json
{ "items": [ { "code": "SKU-001", "name": "…", "price": 12.50, "availableQuantity": 10 } ],
  "pageSize": 50, "hasMore": true, "nextCursor": "eyJ2IjoxLC…", "total": 12340 }
```

`total` is the count for the current filter and is present **only on the first page** (no
cursor) — one `COUNT_BIG` per new query, not per page. `hasMore` comes from fetching
`pageSize + 1` rows, never from the count.

Validation failures use the existing ProblemDetails shape: `pageSize` / `sort` / `search` under
`errors`, a bad cursor as `400` with `code: paging.invalid_cursor`.

## Why keyset, not offset

`OFFSET n ROWS` reads and discards `n` rows on every request — page 1,000 costs 1,000× page 1 —
and shifts under concurrent inserts, so a walker sees duplicates or gaps. A keyset cursor
("everything after the last row I saw") costs one index seek per page regardless of depth and
is stable under inserts and under the stock updates that run all day on this table. The price is
no "jump to page 37"; the UI gets Previous/Next and a page counter, which is what a catalogue
needs. Say this in the README.

## Cursor — `Application/Abstractions/Paging/PageCursor.cs`

Opaque base64url of a small JSON document:

```json
{ "v": 1, "f": "name", "d": "asc", "k": "Widget", "c": "SKU-017", "h": "3f2a…" }
```

`f`/`d` = sort field and direction, `k` = the last row's sort value (string; `price` as an
invariant decimal string), `c` = the last row's code (the tie-breaker), `h` = first 8 hex chars of
SHA-256 over `search|inStock`. On decode the server checks `v`, and that `f`, `d`, `h` equal the
current request; any mismatch, bad base64 or bad JSON → `paging.invalid_cursor`. No signature: a
forged cursor can only choose a different starting row, which the caller could do anyway.

`Page<T>` and `PageCursor` are generic and live in `Application/Abstractions/Paging/` — the next
paged list (orders, if ever) reuses them unchanged.

```csharp
public sealed record Page<T>(IReadOnlyList<T> Items, int PageSize, bool HasMore, string? NextCursor, long? Total);
```

## Query — `Features/Products/GetProducts/GetProductsQuery.cs`

```csharp
public sealed record GetProductsQuery(string? Search, bool InStock, string Sort, int PageSize, string? Cursor)
    : IQuery<Page<ProductDto>>;
```

The validator (FluentValidation, same file — `ValidationBehavior` already runs for queries)
enforces the table above. The handler decodes the cursor (`paging.invalid_cursor` on failure) and
calls `IProductQueryRepository.GetPageAsync(criteria, ct)`.

## SQL — `Infrastructure/Read/ProductQueryRepository.cs`

One round trip, `QueryMultipleAsync`: the page, and on a first page the count. The seek predicate
and `ORDER BY` are assembled from a **whitelist**, never from the request string:

```csharp
private static readonly IReadOnlyDictionary<string, string> SortColumns = new Dictionary<string, string>
{ ["code"] = "code", ["name"] = "name", ["price"] = "price" };
```

```sql
-- sort=name asc, second page; the seek is the expanded row-value comparison (T-SQL has no tuple <)
SELECT TOP (@take) code AS Code, name AS Name, price AS Price, available_quantity AS AvailableQuantity
  FROM products
 WHERE (@prefix IS NULL OR code LIKE @prefix ESCAPE '\' OR name LIKE @prefix ESCAPE '\')
   AND (@inStock = 0 OR available_quantity > 0)
   AND (@k IS NULL OR name > @k OR (name = @k AND code > @c))
 ORDER BY name ASC, code ASC;

-- first page only
SELECT COUNT_BIG(*) FROM products
 WHERE (@prefix IS NULL OR code LIKE @prefix ESCAPE '\' OR name LIKE @prefix ESCAPE '\')
   AND (@inStock = 0 OR available_quantity > 0);
```

- `@take = pageSize + 1`; the extra row sets `HasMore` and is dropped.
- `@prefix = Escape(search) + '%'` where `Escape` doubles-up `\` and escapes `%`, `_`, `[`.
  Prefix match is sargable on the indexes below; a contains-match on `name` would need full-text
  search and is out of scope — say so in the README.
- `desc` flips every comparison and both `ORDER BY` directions; `code` sort needs only `code > @c`.
- `sort=code` is the default because it is the clustered key: no extra index, no lookup.

## Migration — `AddCatalogueIndexes`

```sql
CREATE INDEX ix_products_name  ON products(name, code);
CREATE INDEX ix_products_price ON products(price, code);
```

No `INCLUDE (available_quantity)` and no index on `available_quantity`: that column is written
by every order, and each extra index that carries it is another page touched inside the hot
transaction Task 12 is trying to shorten. A 50-row key lookup per page is cheaper than that.
`inStock=true` therefore filters after the seek; fine for a narrow table, documented.

## Output cache (Task 13)

The `catalogue` policy **must** vary by all five query keys:

```csharp
p.Expire(TimeSpan.FromSeconds(1)).Tag("catalogue").SetVaryByQuery("search", "inStock", "sort", "pageSize", "cursor")
```

Do not rely on the default vary rules. If Task 13 is already done when this task runs, add it
here; if not, Task 13's spec carries the same line. Either way, write the collision test below.

## Seed — `dotnet run --project src/Ordering.Api -- seed --products N`

`LOAD-000001 … LOAD-{N}`, names drawn from a small word list so there are ties and prefixes
(`"Widget Blue"`, `"Widget Red"` …), prices spread across a range, `available_quantity` = 1,000
(so `inStock` filters are not trivial). Guarded per code like the two brief products; a re-run
adds nothing. Uses `SqlBulkCopy` in batches of 10,000 — `N = 100000` must finish in seconds, not
minutes. Task 12 uses the same switch for its load catalogue; whichever task runs first adds it,
the other reuses it.

## Definition of done

- [ ] 100,000 products seeded once per test collection (`SqlBulkCopy` in the fixture); `dotnet test` wall time recorded in the README
- [x] Walking every page for `sort=name` and for `sort=price:desc` (`pageSize=200`) yields exactly `total` codes, no duplicate, no gap — **while** a background task updates `available_quantity` of random products the whole time
- [x] 1,000 products sharing one name come back across page boundaries in code order, none missing
- [x] Cursor from `sort=name` sent with `sort=price`, a truncated cursor, and a cursor for a different `search` → `400 paging.invalid_cursor`
- [x] `pageSize=0`, `pageSize=201`, `sort=stock` → `400` with the field named under `errors`
- [x] A product named `50%_off` is found by `search=50%` and not by `search=50` + any other prefix collision you construct; `search=[` does not throw
- [x] `total` present only on the first page; `hasMore` false on the last page and `nextCursor` null
- [x] Deep page (walk 1,000 pages by name) p95 < 200 ms in the test; the real number recorded in `docs/capacity.md` under "read path"
- [ ] With the output cache on, page 1 and page 2 of the same query return different bodies (vary-by-query works)
- [x] The Task 3 test for exact decimals still passes against the paged shape (`items[].price`)
- [x] Swagger shows the five parameters and the `Page<ProductDto>` schema; `GET /api/products` with no parameters returns 50 rows, never the table

Status (owner instruction "skip tests"): no test code was written. Ticked boxes were verified by
hand against the compose database with 100,002 products (numbers in `docs/capacity.md`, "Read
path"); the "while a background task updates stock" clause and the p95 assertion are not yet
tests. Left open: the 100,000-row fixture + `dotnet test` wall time, and the output-cache
collision test (Task 13 is not built; the endpoint carries the `// Task 13:` seam).
