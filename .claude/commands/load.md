---
description: Run a k6 scenario against the running API and record the numbers
---

Run the load scenario named in $ARGUMENTS (`hot`, `spread`, `multi`, `read-products`,
`read-order`; default `hot`) from `tools/load/` against the API at `API_BASE`
(default `http://host.docker.internal:5000`), through `grafana/k6` in Docker — see
@docs/tasks/task-12-write-path.md for the scripts and the seeding command.

Before the run: confirm the API is up (`/health` → 200), the load catalogue exists
(`GET /api/products` lists `LOAD-0001`), and take a `tools/load/waits.sql` snapshot.
After the run: take a second snapshot and subtract.

Report, in this order:
- requests/s achieved at each arrival-rate stage and where p95 crossed 500 ms
- response code distribution (201 / 200 / 409 by `code` / 503 by `code` / 5xx — 5xx must be zero)
- top three wait types by delta and what each one means for this system
- the exact `docker run` command used and the commit hash

Then add or update the matching row in `docs/capacity.md`. Never edit an older row — add a new
one. If the numbers are worse than the previous row, say so plainly and do not tune anything in
this command; that is a task's job.
