---
command: link related
group: links
summary: Add a discovered symmetric native association through a verified Core proposal.
stability: experimental
mutates: ado
---

# `twig link related`

Add a symmetric association between published items in an explicitly initialized
Core ADO workspace. The relation reference comes from the discovered schema,
not a hardcoded process or native name.

```
twig link related <targetId> [--id <n>] [-c <comment>] [-o <format>]
```

`targetId` names the other endpoint; `--id` names the source. Only human output
may use the selected Core item. Both IDs must be positive native identities.
`-c`/`--comment` is retained syntax but **unsupported**: any supplied value,
including an empty string, refuses before HTTP. The typed relationship effect
has no comment payload; the command never silently drops it or emits a separate note.

One discovered non-directional association is required. The source's observed
revision binds one add-link PATCH. The shared proposal/journal workflow requires
independent acknowledgement and fresh reciprocal readback, not a best-effort resync.

## Examples

```text
twig link related 615
twig link related 615 --id 619 -o json
```

Success exits `0` with `linkAdded`, `status: Verified`, proposal digest and
native link records. Duplicate adds, unsupported comments, missing observations
and unresolved outcomes exit `1`; invalid/self/missing machine targets exit `2`.

See [shared behavior and output](./README.md),
[`link unrelate`](./link-unrelate.md) and [`link unlink`](./link-unlink.md).
