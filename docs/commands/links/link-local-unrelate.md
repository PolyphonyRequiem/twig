---
command: link local unrelate
group: links
summary: Tombstone a LOCAL Related association with exact Core local CAS.
stability: stable
mutates: local
---

# `twig link local unrelate`

```text
twig link local unrelate <target> --id <source> [-o human|json|json-full|json-compact|minimal]
```

Use explicit native numeric locators, negative Core seed aliases or original
canonical lowercase GUIDv7s. `--id` is mandatory for every write, including
human output. The old two-GUID positional syntax is retired.

The same discovered local-only symmetric Association and live endpoint evidence
as [`related`](./link-local-related.md) are required. No native capability,
provider error or connection change authorizes fallback. Native credential,
schema and read acquisition may occur, but **no native mutation** occurs.

Removal stages one existing Core `Unrelate` operation with the exact edge
`ExpectedLocalVersion`, then uses the shared digest-bound proposal/apply/verify
journal. It persists a tombstone with an incremented local version. It never
deletes a row, rewrites original seed UUID edge keys, or alters provider-origin
relationships. `Verified` means local CAS, not tracker publication.
JSON `source`/`target` preserve the original stored identities as typed objects:
`localId` for a local UUID, or `provider`/`scope`/opaque `nativeId` for a remote
key. Publication never replaces an existing edge's UUID object with its mapping.

Absent/deleted edges, stale versions, multiple logical matches, missing endpoint
evidence, policy/actor mismatches and unresolved effects refuse explicitly.
No fake Verified no-op is emitted. A subsequently resurrected edge must bind
the tombstone's exact version, preventing stale ABA writes.

## Examples

```text
twig link local unrelate 43 --id 42
twig link local unrelate 019a0000-0000-7000-8000-000000000002 --id 42 -o json
```

## Exit codes

- `0`: the tombstone is Verified and journaled.
- `1`: absent/conflicting/stale edge, unsupported declaration, missing evidence,
  authority/policy/fence refusal or storage failure.
- `2`: invalid/missing/conflicting arguments or unsupported output (`ids` refused).

[`link local list`](./link-local-list.md) is the separate zero-HTTP cache read.
