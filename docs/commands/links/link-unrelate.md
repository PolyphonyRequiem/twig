---
command: link unrelate
group: links
summary: Remove a discovered symmetric association through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link unrelate`

Remove an existing native association between published Core ADO items.
[`link unlink related`](./link-unlink.md) uses the same owner and effect.

```
twig link unrelate <targetId> [--id <n>] [-o <format>]
```

`targetId` names the other endpoint; `--id` names the source. Only human output
may use selected Core context. Both IDs must be positive native identities.
One unambiguous discovered symmetric association and both observed endpoints
are required.

The source's exact observed revision and native relation index bind one removal
PATCH. Independent readback must certify absence on both endpoints; a failed,
partial or unknown result is not success and never authorizes automatic retry.

## Examples

```text
twig link unrelate 615
twig link unrelate 615 --id 619 -o minimal
```

Verified removal exits `0` (`linkRemoved`). An absent edge, unavailable evidence,
revision conflict or unresolved outcome exits `1`. Invalid/self/missing machine
targets exit `2`. See [shared behavior and output](./README.md) and
[`link related`](./link-related.md).
