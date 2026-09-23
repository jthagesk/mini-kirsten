---
name: critical-review
description: Review the implementation and diff before delivery.
---

# Critical review

Run this checklist after implementation and before delivery:

1. Map every acceptance criterion to a changed line. Mark each PASS or FAIL.
2. Check empty, missing, malformed, error, null, zero, and oversized inputs.
3. Remove unrelated edits, formatting churn, unused imports, and speculative cleanup.
4. Reject deleted or weakened tests, changed expectations, swallowed exceptions,
   and hardcoded shortcuts.
5. Check naming, comments, and behavior for maintainability.
6. Fix findings, run verification, and report unresolved uncertainty.
