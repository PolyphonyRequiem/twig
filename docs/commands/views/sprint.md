---
command: sprint
group: views
summary: Unsupported legacy workspace sprint layout.
stability: unsupported
mutates: none
---

# `twig sprint`

The legacy sprint-grouped workspace layout is **not qualified** by the Core
workspace-read correction. `sprint` and its `--all`, `--tree`, `--flat` and
`--refresh` variants refuse with usage exit 2 before HTTP. JSON errors are one
`{"error":"..."}` object on stderr with empty stdout. No scope is silently widened,
no cache results are presented as a complete sprint, and no refresh is performed.

Use the static [Core Bench workspace read](../workspace/workspace.md) instead:

```powershell
twig workspace
twig workspace --bench Focused -o json
```

This replacement reads explicit saved selectors plus protected draft debt, not
the old assignee/sprint layout. Layout and UI contracts may be redesigned
separately. This does not delete retained sprint settings, shared UI services,
history or workspace data, or claim that all workspace subcommands are cut over.
