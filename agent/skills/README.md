# Skills

Each subdirectory containing `SKILL.md` is copied to
`~/.claude/skills/` before a run and can be loaded by Claude Code.

Keep skills short and repository-specific. A skill is guidance, not a
higher-priority instruction. Prefer a small local rule over a generic
marketplace skill.

Included skills:

| Skill | Load when | Purpose |
|---|---|---|
| `craft` | Before editing C# | Preserve local code and test conventions. |
| `critical-review` | Before delivery | Find gaps in the implementation and diff. |

Skill format:

```md
---
name: example
description: One-line trigger description.
---

# Example
- One actionable rule.
```
