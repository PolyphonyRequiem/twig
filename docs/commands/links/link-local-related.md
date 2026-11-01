---
command: link local related
group: links
summary: Verify an explicit LOCAL Related association through Core local CAS.
stability: stable
mutates: local
---

# `twig link local related`

```text
twig link local related <target> --id <source> [-o human|json|json-full|json-compact|minimal]
```

Both endpoints are explicit: use a native numeric locator, a negative Core seed
alias, or an original canonical lowercase GUIDv7. `--id` is required in human
and machine output; active context is never consulted. The old two-GUID
positional syntax and separate `LocalWorkItemId` registry are retired.

The pinned Core provider must discover exactly one symmetric Association declared
local-only, not native. GitHub's declared local Related kind qualifies; ADO's
native Related kind does not. Native failure, unavailable metadata or a denied
read never enables a local fallback. Mutation may acquire credentials, schema
metadata and native endpoint observations; **zero native mutations**, not zero
provider contact, is the guarantee. Numeric writes freshly requalify the actual
repository number even on a cache hit.

One existing Core proposal carries one local-origin `Relate` operation. The shared
prepare/apply/verify workflow retains its digest and journal. `Verified` means
**local CAS**, never a native tracker link. Output identifies every edge as
`LOCAL`, `local-only`, `non-native`. Provider-origin edges are untouched.
JSON `source`/`target` are exact stored identity objects: a local endpoint has
`localId` (canonical lowercase GUID), while a remote endpoint has `provider`,
`scope`, and opaque `nativeId`. They are not debug strings or numeric locators.

An initial edge has no expected local version. Resurrection binds the exact
retained tombstone version and increments it; there is no delete/compaction.
Duplicate active edges, self-links and multiple logical matches refuse without
claiming a new Verified effect. UUIDs need a live Core seed or their original
Verified publication mapping and observed native endpoint. Historical proposals
or an alias alone are not live evidence. Publication keeps original UUID edge
keys, mappings, proposal bytes and history; subsequent number/UUID targeting
resolves logical identity without writing a parallel edge.

## Examples

```text
twig link local related 43 --id 42
twig link local related 019a0000-0000-7000-8000-000000000002 --id 42 -o json
```

## Exit codes

- `0`: the local effect is Verified and journaled.
- `1`: missing live evidence, unsupported schema/capabilities, duplicate/conflicting
  edges, stale CAS, policy, authority or unresolved-effect fence, or storage failure.
- `2`: missing/invalid/conflicting arguments or unsupported output (`ids` drops
  mutation evidence and is refused).

See [`link local list`](./link-local-list.md) and
[`link local unrelate`](./link-local-unrelate.md).
