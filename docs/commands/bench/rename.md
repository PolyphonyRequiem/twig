---
command: bench rename
group: bench
summary: Rename a user-wide Bench without changing its pins or context selections.
stability: stable
mutates: local
---

# `twig bench rename`

Rename the native shared definition. Its GUID, connection-qualified pins and
repository/worktree selections remain unchanged. No initialized workspace,
provider credentials or work HTTP is required.

```powershell
twig bench rename "Release" "Delivery"
twig bench rename <guid> "Delivery" --expect-contents <digest> -o json
```

`name` is an existing name (case-insensitive) or canonical GUID. `newName` must be
trimmed, nonempty, at most 120 characters and unique under Unicode-normalized,
case-insensitive comparison.

`--expect-bench <guid>` refuses a different identity under the same name.
`--expect-contents <digest>` refuses changed contents; obtain the complete digest
from `twig bench list --include-management -o json`. The final write checks the
captured revision, so a concurrent change is never overwritten.

`-o|--output` supports `human`, JSON variants and `minimal`. Success reports the
Bench's ID, new name and new revision. Errors use the selected output format on
stderr: `0` success, `1` missing/conflicting/unavailable state, `2` invalid input.

See [`bench list`](./list.md), [`bench create`](./create.md) and
[`bench delete`](./delete.md).
