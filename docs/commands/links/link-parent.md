---
command: link parent
group: links
summary: Set the parent of an unparented published item through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link parent`

## Behavior

Attach a parent to an unparented published item in an explicitly initialized
Core ADO workspace. Existing parents are never silently replaced.

```
twig link parent <parentId> --id <childId> [-o <format>]
```

The first positional ID names the parent. For human output, a second positional
ID or `--child` can name the child instead of `--id`. Every machine output
requires `--id`. Conflicting child IDs refuse before HTTP. Both IDs must be positive.

The command discovers one directed hierarchy and observes both endpoints.
Canonical direction is **parent -> child**: the parent's actual observed revision
binds one native forward-link PATCH. Shared proposal validation, native rules,
durable intent, acknowledgement and independent reciprocal readback determine
success. No native relation reference or revision is guessed.

## Examples

```text
twig link parent 5678 1234
twig link parent 5678 --id 1234 -o json
```

Success exits `0` with `linkParented`, `status: Verified`, the proposal digest
and current native links. Duplicate adds, existing other parents, missing
observations and unresolved effects exit `1`; invalid/self/conflicting targets
or missing targets exit `2`.

See [shared behavior and output](./README.md),
[`link unparent`](./link-unparent.md) and the explicitly unsupported
[`link reparent`](./link-reparent.md).
