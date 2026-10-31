---
command: link successor
group: links
summary: Mark a published item as blocking another through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link successor`

The selected item must complete before the named target. Requires an explicitly
initialized Core ADO workspace and one discovered directed dependency kind.

```
twig link successor <targetId> [--id <n>] [-o <format>]
```

`targetId` names the blocked item; `--id` names the blocker. Only human output
may use the selected Core item. Both targets must be positive native IDs.

Canonical direction is **blocker -> blocked**. The selected blocker's observed
revision binds one forward-link PATCH. Shared proposal validation, declared policy,
durable journal intent, independent acknowledgement readback and fresh reciprocal
observations determine success. No legacy writer or sync fallback is used.

## Examples

```text
twig link successor 66
twig link successor 66 --id 65 -o json
```

Verified success exits `0` (`linkAdded`), including `status: Verified`, digest
and current native links. Duplicate edges, unavailable evidence and unresolved
outcomes exit `1`; invalid/self/missing machine targets exit `2`.

See [shared behavior](./README.md), [`link predecessor`](./link-predecessor.md)
and [`link unlink`](./link-unlink.md).
