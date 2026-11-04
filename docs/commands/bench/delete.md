---
command: bench delete
group: bench
summary: Delete a shared Bench; one holding pins requires its exact name as confirmation.
stability: stable
mutates: local
---

# `twig bench delete`

Remove a native user-wide Bench definition. Work items, drafts, proposals,
journals, workspace exclusions and other Benches are untouched.

```powershell
twig bench delete "Empty collection"
twig bench delete "Release" --confirm "Release" -o json
```

`name` is an existing Bench name (case-insensitive) or canonical GUID. Empty
Benches can be deleted directly. A Bench holding item pins, subtree pins or query
rules refuses until `--confirm` exactly repeats its saved name. There is no
`--force` and no undeletable automatically created default Bench.

`--expect-bench <guid>` refuses deletion/recreation under the same name.
`--expect-contents <digest>` checks the complete captured definition; obtain it
from `twig bench list --include-management -o json`. The final transaction checks
the captured revision, so a concurrent change is not deleted.

Deletion is shared everywhere that Bench is used. Worktree selections are not
silently redirected to another Bench; select an existing one explicitly. A
durable tombstone prevents an old workspace import or reused creation GUID from
resurrecting the deleted definition.

`-o|--output` supports `human`, JSON variants and `minimal`. Success reports the
deleted ID, saved name and deletion revision. Errors use the selected format on
stderr: `0` success, `1` absent/conflicting/unconfirmed/unavailable state, `2`
invalid input.

See [`bench list`](./list.md), [`bench create`](./create.md),
[`bench rename`](./rename.md) and [`bench switch`](./switch.md).
