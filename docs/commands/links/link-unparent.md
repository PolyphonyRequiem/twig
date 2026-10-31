---
command: link unparent
group: links
summary: Remove the one observed native parent through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link unparent`

Remove the single observed parent of a published Core ADO item. This is a
standalone removal, not the first half of an automatic reparent operation.

```
twig link unparent [child] [--id <n>] [--child <n>] [-o <format>]
```

`child` (positional or `--child`) or `--id` selects a positive native child.
Machine output requires `--id` or `--child`, not just the positional. Only human
output may resolve the selected Core item. Conflicting targets refuse
before HTTP.

One discovered directed hierarchy and exactly one observed native parent are
required. The canonical edge is **parent -> child**, so the parent's freshly
observed revision binds the exact relation-index removal PATCH. Both endpoints
must separately certify the removal before success; a changed revision refuses
without retry.

## Examples

```text
twig link unparent
twig link unparent --id 1234 -o minimal
```

Verified removal exits `0` (`linkUnparented`). No/ambiguous parent, missing
observations or unverified outcomes exit `1`. Invalid/conflicting targets,
machine ambient targeting and bare-ID output exit `2`.

See [shared behavior and output](./README.md),
[`link parent`](./link-parent.md) and the unsupported
[`link reparent`](./link-reparent.md).
