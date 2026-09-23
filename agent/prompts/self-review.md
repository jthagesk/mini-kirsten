Review and, if necessary, repair an implementation for the following issue in
`{Repo}` on branch `{BranchName}`.

## Issue #{IssueNumber}: {IssueTitle}

{IssueBody}

## Previous report

{PreviousOutput}

## Review

- Inspect `git diff` and map every acceptance criterion to a changed line.
- Remove unrelated changes.
- Reject tests that only restate implementation details.
- Restore any deleted, weakened, or bypassed verification.
- Check empty, malformed, missing, error, null, zero, and oversized inputs.
- Fix findings and run the configured verification commands.
- Do not commit or push.
- Output only the final report format from `system.md`; include a PASS/FAIL
  list under `## Verification`.
