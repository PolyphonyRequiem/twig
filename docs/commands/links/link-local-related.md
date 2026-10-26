---
command: link local related
group: links
summary: Add an explicit Twig-local Related link between two local work items.
stability: stable
mutates: local
---

# `twig link local related`

Create a symmetric Related edge in Twig's durable local relationship store,
using two canonical lowercase local GUIDv7 identities:

```
twig link local related <source-local-guid> <target-local-guid> [-o human|json|minimal]
```

Both identities must resolve to live local work-item records. The active
provider's static declaration must say that Related is `unsupported`. A native,
emulated, unavailable, or unknown capability is not a reason to create a local
edge; the command never probes or writes the provider.

The edge is stored once under the provider-neutral local identities with
`origin: twig-local`, `storage: local-only`, and
`publication: not-applicable`. It remains attached to those local identities
across connection changes. Twig does not translate, copy, or publish it when a
provider connection changes.

## Examples

Create an explicit local association:

```
twig link local related 019a0000-0000-7000-8000-000000000001 019a0000-0000-7000-8000-000000000002
```

Emit a machine-readable result:

```
twig link local related 019a0000-0000-7000-8000-000000000001 019a0000-0000-7000-8000-000000000002 -o json
```

## Results

An existing edge is reported as `already-present` without changing its local
revision. Self-links, malformed identities, missing or deleted local items,
non-unsupported provider declarations, and concurrent-write conflicts are
reported as errors. No provider write or synchronization occurs.

See [`link local list`](./link-local-list.md) and
[`link local unrelate`](./link-local-unrelate.md).
