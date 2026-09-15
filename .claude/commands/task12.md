---
description: Task 12 — write path: measure, then RCSI, fast-fail read, one-round-trip deduct
---

## Task 12

Read, in this order:
1. @CLAUDE.md — architecture, conventions and hard rules
2. @docs/architecture.md — the design decisions this task implements
3. @docs/tasks/task-12-write-path.md — the spec for this task

Then implement it completely.

**Focus:** Baseline row in `docs/capacity.md` before any change. One change per commit, measured after each. Nothing here may alter what a 201 or 409 means; the conditional UPDATE and the CHECK still decide stock.

### How to work

- Restate the task in 3 bullets and list the files you will add or change. Wait for nothing —
  then build it.
- Follow the abstractions in `Application/Abstractions` and `web/order-console/src/app/lib`.
  If you need plumbing that already exists, reuse it; if you write it twice, extract it.
- Write the tests named in the spec's "Definition of done" and run them.
- Do not start work belonging to another task. If you hit a seam, leave a `// Task N:` comment.

### Finish with

- `dotnet build` and `dotnet test` green (or `npm run lint && npm run build` for frontend tasks)
- Tick the "Definition of done" boxes in `docs/tasks/task-12-write-path.md`
- A short report: what you built, decisions you made, anything you assumed (assumptions also go
  into README.md)

$ARGUMENTS
