---
description: Task 5 — idempotency lifecycle
---

## Task 5

Read, in this order:
1. @CLAUDE.md — architecture, conventions and hard rules
2. @docs/architecture.md — the design decisions this task implements
3. @docs/tasks/task-05-idempotency.md — the spec for this task

Then implement it completely.

**Focus:** Unique index enforces it, not a read-then-write check. Key row goes in the same `SaveChanges` as the order; the stock `UPDATE` is the last statement before commit. Define payload equivalence precisely.

### How to work

- Restate the task in 3 bullets and list the files you will add or change. Wait for nothing —
  then build it.
- Follow the abstractions in `Application/Abstractions` and `web/order-console/src/app/lib`.
  If you need plumbing that already exists, reuse it; if you write it twice, extract it.
- Write the tests named in the spec's "Definition of done" and run them.
- Do not start work belonging to another task. If you hit a seam, leave a `// Task N:` comment.

### Finish with

- `dotnet build` and `dotnet test` green (or `npm run lint && npm run build` for frontend tasks)
- Tick the "Definition of done" boxes in `docs/tasks/task-05-idempotency.md`
- A short report: what you built, decisions you made, anything you assumed (assumptions also go
  into README.md)

$ARGUMENTS
