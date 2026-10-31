---
command: link unlink
group: links
summary: Remove a native predecessor, successor or related edge through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link unlink`

Remove a published Core ADO dependency or association using the same discovered
shape and canonical direction as its corresponding add command.

```
twig link unlink <linkType> <targetId> [--id <n>] [-o <format>]
```

`linkType` accepts `predecessor`, `successor` or `related` (case-insensitive).
`targetId` names the other endpoint; `--id` selects the source. Only human output
may resolve selected Core context. Hierarchy names are refused: use
[`link unparent`](./link-unparent.md).

Predecessor removal targets the blocker with its observed revision; successor
and association removal target the selected source. The provider rechecks the
exact revision and relation index before one PATCH, then separately verifies
the acknowledgement and fresh reciprocal observations through the shared journal.

## Examples

```text
twig link unlink predecessor 65
twig link unlink related 615 --id 619 -o json
```

Verified removal exits `0` (`linkRemoved`). Missing edges/evidence, revision
conflicts and unresolved outcomes exit `1`; unknown link types, invalid/self
targets and missing machine targets exit `2`.

See [shared behavior and output](./README.md), [`link predecessor`](./link-predecessor.md),
[`link successor`](./link-successor.md) and [`link unrelate`](./link-unrelate.md).
