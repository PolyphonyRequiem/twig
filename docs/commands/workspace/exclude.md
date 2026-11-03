---
command: workspace exclude
group: workspace
summary: Hide a work item from this workspace view without changing shared Bench pins.
stability: stable
mutates: local
---

# `twig workspace exclude`

Save an exclusion for the current initialized workspace and its admitted
connection. This changes presentation, not shared Bench membership or upstream
work items. Another worktree using the same Bench retains its own view rules.

```powershell
twig workspace exclude 3980
twig workspace exclude 4102 -o json
```

The positional `id` must be positive. Local seeds and invalid IDs are refused.
ADO IDs need no work HTTP; GitHub resolves an already cached, typed issue number
to its canonical native identity. No credentials, account selection, active item
or current Bench are changed.

Repeatedly excluding the same item is a successful no-op. Rules are persisted
in the native user store, keyed by workspace root and connection, with captured
revision checks. They are applied after Bench membership resolution.

Protected drafts are not discarded or silently hidden: excluded items with
local changes remain visible as protected work and are marked `isExcluded`.
The shared Bench's saved item/tree/query/area/iteration selections are unchanged.

`-o|--output` supports `human`, JSON variants and `minimal`. Changes report the
remaining exclusion count and a message. Errors are format-aware on stderr:
`0` success, `1` unavailable/conflicting state, `2` invalid input.

See [`workspace exclusions`](./exclusions.md),
[`workspace`](./workspace.md) and [`bench`](../bench/README.md).
