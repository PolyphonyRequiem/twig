---
command: link reparent
group: links
summary: Parent replacement is unsupported; refuses without removing any link.
stability: experimental
mutates: none
---

# `twig link reparent`

The syntax is retained, but parent replacement is **not qualified** by the
current Core relationship contract. Every invocation refuses before native HTTP.
No existing parent is removed, no successor request is sent, and no retry is implied.

```
twig link reparent <targetId> [child] [--id <n>] [--child <n>] [-o <format>]
```

`targetId` is the proposed new parent; `child` (positional or `--child`) and
`--id` name the child. Conflicting child/ID values are usage errors.
See [shared targeting, output and safety](./README.md).

The old implementation removed and then added links in two HTTP requests. It
was not atomic: a failed add could leave the child parentless. Core has no typed
single-request replacement effect, and this command does not fake one with a
multi-operation proposal or fall back to the old writer.

## Examples

```text
twig link reparent 5678
twig link reparent 5678 --id 1234 -o json
```

Both report the unsupported replacement with exit `1`. Invalid targets,
conflicting targets, missing machine targets and bare-ID output exit `2`.
For an item that has **no parent**, use [`link parent`](./link-parent.md).
An explicit [`link unparent`](./link-unparent.md) is a separate, independently
verified removal, not a transactional replacement workflow.
