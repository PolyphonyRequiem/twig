---
command: tree
group: views
summary: Hidden targeted-tree alias; legacy --all workspace layout is unsupported.
stability: stable
mutates: local-on-explicit-refresh
---

# `twig tree`

Without `--all`, the hidden `tree` alias retains its existing targeted-tree
route, equivalent to [`show --tree`](../context/show.md). This correction does
not rewrite targeted tree behavior or rendering.

```powershell
twig tree 1234 --depth 2
twig tree --id 1234 -o json
```

Supply a positional ID or `--id` for structured output. Conflicting explicit
targets refuse. Human targeting may use the active item. `--depth`,
`--no-live` and explicit `--refresh` retain their targeted-route meanings.

`tree --all`, including `--refresh`, now goes to the workspace owner's explicit
usage refusal **before HTTP**. It does not render all cached items, query the
team, silently ignore the target or substitute another scope. The same applies
to `workspace --tree`, `workspace --view tree` and the old sprint layout.
The workspace UI and full-backlog contract remain unqualified.

Use [`workspace`](../workspace/workspace.md) for a static Core Bench table,
including all protected draft debt, or targeted `show <id> --tree` for an item
hierarchy. Unsupported workspace shapes return 2 with format-aware stderr and
empty stdout; ordinary targeted-route exit behavior is unchanged.
