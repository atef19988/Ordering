# Capacity

Measured numbers only; every row names the command that produced it, the commit and the
machine. Estimates are not allowed in this file. The write-path tables (Task 12) are added by
that task, before its first change.

Machine for every row unless stated otherwise: Intel Core i7-4790 @ 3.60 GHz, 32 GB, Windows 11
Pro 22621, Docker Desktop (engine 28.0.1); SQL Server 2022 in compose on the same machine; API
`dotnet run` (Development) on the same machine; client is a Python script on the same machine,
so the numbers include loopback HTTP and the client's own JSON parsing.

## Read path

### Catalogue paging (Task 15)

Catalogue: 100,002 products (`dotnet run --project src/Ordering.Api -- seed --products 100000`
+ the two brief products), 80 distinct names (1,250 rows per name), 10,000 distinct prices.
Walker: `GET /api/products?sort=…&pageSize=…&cursor=…` page after page until `hasMore` is
false (the script under "Command"). No output cache yet (Task 13 runs after this task), so every
request is a Dapper round trip.

| Walk | Pages | p50 / page | p95 / page | Rows = total, no dup, no gap | Commit | Notes |
|---|---|---|---|---|---|---|
| `sort=name`, `pageSize=200` | 501 | 6.6 ms | 9.9 ms | yes (100,002) | 3650a66 + Task 15 | seek on `ix_products_name` |
| `sort=price:desc`, `pageSize=200` | 501 | 6.3 ms | 8.0 ms | yes (100,002) | 3650a66 + Task 15 | seek on `ix_products_price`; see the note below |
| `sort=name:desc&inStock=true&search=wid`, `pageSize=200` | 50 | 6.0 ms | 12.4 ms | yes (10,000) | 3650a66 + Task 15 | prefix + unindexed stock filter |
| `sort=name`, `pageSize=100` (deep walk, 1,001 pages) | 1,001 | 4.4 ms | 6.0 ms | yes (100,002) | 3650a66 + Task 15 | page 1,000 costs the same as page 2 |

Latency is flat with depth — page 500 by name: 6 ms; page 2: 8 ms — which is the whole point of
keyset over offset.

**Note on the `price` sort.** The first measurement of `sort=price:desc` was 40.9 ms p50 /
71.3 ms p95 and *grew with depth* (page 100: 21 ms, page 500: 69 ms): the seek parameter was
sent as a plain `DbType.Decimal`, so SQL Server compared `price` against a wider decimal and
walked the index instead of seeking. Sending `@afterKey` as `decimal(18,2)` — the column's own
precision and scale — gave the row above (6.3 ms / 8.0 ms, flat: page 100 … 500 all 6–8 ms).
Kept here as the before/after that CLAUDE.md asks for.

Seed: 100,000 products in 4.3 s (`SqlBulkCopy` into a temp table, 10 batches of 10,000, guarded
`INSERT … WHERE NOT EXISTS`); a re-run adds 0 rows in 1.4 s.

Command (API on `http://localhost:5050`, script kept out of the repo — it is twelve lines of
`urllib`; the integration-test harness will carry the same walk):

```
for each sort in [name, price:desc]:
    cursor = null; codes = []
    loop: GET /api/products?sort=<sort>&pageSize=200[&cursor=<cursor>]
          assert 200; assert total present only when cursor is null
          codes += items[].code; cursor = nextCursor; break when !hasMore (then nextCursor is null)
    assert len(codes) == total == len(set(codes))
```
