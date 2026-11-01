---
command: link predecessor
group: links
summary: Mark a published item as blocked by another through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link predecessor`

The named target must complete before the selected item. Requires an explicitly
initialized Core ADO workspace and one discovered directed dependency kind.

```
twig link predecessor <targetId> [--id <n>] [-o <format>]
```

`targetId` names the blocker; `--id` names the blocked item. Only human output
may use the selected Core item. Both targets must be positive native IDs.

Canonical direction is **blocker -> blocked**. The command therefore mutates
`targetId` using that blocker's actual observed revision, not the blocked item's
revision or a guessed inverse relation reference. The shared proposal and journal
path verifies the immutable acknowledgement and fresh reciprocal observations.
It does not invent transition or dependency-cycle capabilities; discovered native
constraints and explicitly declared policy determine admission.

## Examples

```text
twig link predecessor 65
twig link predecessor 65 --id 66 -o minimal
```

Verified success exits `0` (`linkAdded`). Duplicate edges, unavailable schema/items
or unverified outcomes exit `1`. Invalid/self/missing machine targets exit `2`.
See [shared behavior](./README.md), [`link successor`](./link-successor.md)
and [`link unlink`](./link-unlink.md).
