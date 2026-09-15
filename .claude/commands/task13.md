---
description: Task 13 — read path: Redis output cache + SSE push over a RabbitMQ fan-out
---

## Task 13

Read, in this order:
1. @CLAUDE.md — architecture, conventions and hard rules
2. @docs/architecture.md — the design decisions this task implements
3. @docs/tasks/task-13-read-path.md — the spec for this task

Then implement it completely.

**Focus:** `IUnitOfWork.OnCommitted` is the only post-commit I/O. Catalogue from Redis with tag eviction, fail-open. SSE streams carry full state, never deltas; hints are best-effort. Redis down must never produce a 500.

### How to work

- Restate the task in 3 bullets and list the files you will add or change. Wait for nothing —
  then build it.
- Follow the abstractions in `Application/Abstractions` and `web/order-console/src/app/lib`.
  If you need plumbing that already exists, reuse it; if you write it twice, extract it.
- Write the tests named in the spec's "Definition of done" and run them.
- Do not start work belonging to another task. If you hit a seam, leave a `// Task N:` comment.

### Finish with

- `dotnet build` and `dotnet test` green (or `npm run lint && npm run build` for frontend tasks)
- Tick the "Definition of done" boxes in `docs/tasks/task-13-read-path.md`
- A short report: what you built, decisions you made, anything you assumed (assumptions also go
  into README.md)

$ARGUMENTS
