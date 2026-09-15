---
description: Stress a concurrency invariant against the running API
---

Write and run a throwaway script that hammers the local API to try to break the invariant named
in $ARGUMENTS (for example: "last unit of SKU-001", "same idempotency key", "double cancel").

Requirements:
- Release all requests from one barrier so they truly overlap
- At least 50 concurrent requests
- Assert the invariant by reading the database directly, not from the HTTP responses
- Run it 5 times; a race that passes once proves nothing

Report: response code distribution, final database state, and whether the invariant held every
run. Delete the script afterwards unless it belongs in the test suite — if it does, say where.
