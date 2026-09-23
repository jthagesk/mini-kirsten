---
name: craft
description: Apply repository coding standards before editing C#.
---

# Craft

- Inspect a nearby implementation and match its structure before editing.
- Use English identifiers, comments, and test names unless an external contract
  requires another spelling.
- Prefer `sealed` classes, primary constructors, expression-bodied members, and
  end-to-end `async`/`await` when consistent with the repository.
- Keep state out of request-scoped components unless required.
- Preserve required JSON fields; fail clearly when a required field is missing.
- Use invariant culture for URLs, query strings, and API payloads.
- Test transformations and pure logic without network calls.
- Keep one behavior per test with Arrange/Act/Assert separation.
- Avoid new packages, unrelated formatting, leftover TODOs, and commits.
