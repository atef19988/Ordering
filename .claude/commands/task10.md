---
description: Task 10 — Angular order form, typeahead picker, paged stock panel, cancel, live status
---

## Task 10

Read, in this order:
1. @CLAUDE.md — architecture, conventions and hard rules
2. @docs/architecture.md — the design decisions this task implements
3. @docs/tasks/task-10-angular-orders.md — the spec for this task

Then implement it completely.

**Focus:** Idempotency key policy is the graded detail — every 503 retries with the same key after `Retry-After`. The catalogue is paged/filtered/sorted on the server: typeahead picker, paged stock panel with URL state, never more than one page in the browser. Live status from the SSE stream. Render every state in the spec.

### How to work

- Restate the task in 3 bullets and list the files you will add or change. Wait for nothing —
  then build it.
- Follow the abstractions in `Application/Abstractions` and `web/order-console/src/app/lib`.
  If you need plumbing that already exists, reuse it; if you write it twice, extract it.
- Write the tests named in the spec's "Definition of done" and run them.
- Do not start work belonging to another task. If you hit a seam, leave a `// Task N:` comment.

### Finish with

- `dotnet build` and `dotnet test` green (or `npm run lint && npm run build` for frontend tasks)
- Tick the "Definition of done" boxes in `docs/tasks/task-10-angular-orders.md`
- A short report: what you built, decisions you made, anything you assumed (assumptions also go
  into README.md)

$ARGUMENTS
