---
command: workspace exclusions
group: workspace
summary: Inspect, remove or clear this workspace's view exclusions.
stability: stable
mutates: local
---

# `twig workspace exclusions`

Inspect the current workspace/connection's view rules. These are separate from
the user-wide Bench definition and never remove shared pins or upstream items.

```powershell
twig workspace exclusions -o json
twig workspace exclusions --remove 3980 -o json
twig workspace exclusions --clear
```

With no change flags, list cached rule metadata (`id`, `kind`, `field` and
`relation`) without work HTTP. `--remove <positive-id>` removes that exact
single-item exclusion, preserving other rules; an absent exclusion is a
successful no-op. `--clear` clears this workspace/connection's rules only.
Combining change flags is a usage error.

`-o|--output` supports `human`, JSON variants and `minimal`. List-only `ids`
emits one numeric work-item ID per line when every rule is an observed
single-item exclusion. It refuses query/tree rules or unavailable native
numbers rather than silently returning a partial ID list.

Changes use the captured rule revision, so concurrent edits are not
overwritten. Selection, work-item state, pending drafts, proposal journals and
other contexts' exclusions are untouched.

Exit codes: `0` success (including empty/no-op); `1` unavailable/conflicting
state; `2` invalid input. Errors use the selected output format on stderr.

See [`workspace exclude`](./exclude.md),
[`workspace`](./workspace.md) and [`bench`](../bench/README.md).
