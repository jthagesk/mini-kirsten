Review the implementation of this issue in `{Repo}` on branch `{BranchName}`. Scope is the diff only.

## Issue #{IssueNumber}: {IssueTitle}

{IssueBody}

## Implementation report

{PreviousOutput}

## Review

1. Run `git diff` once. It is the only code you review.
2. Read only files in the diff, and only the changed regions (`Read` with offset/limit). Use `Grep` only to check callers of a changed symbol. Never explore other files.
3. Map each acceptance criterion to a changed line: PASS or FAIL.
4. FAIL, deleted or weakened test, unrelated change, or unhandled empty/null/malformed input: fix it with a minimal `Edit`.
5. After an edit, run `dotnet build --nologo -v q` and `dotnet test --nologo -v q` once. No edit: do not rerun them; reuse the results from the report.
6. Do not commit or push.

Output only the final report format from `system.md`. Under `## Verification`, list `AC<n>: PASS|FAIL` per criterion.
