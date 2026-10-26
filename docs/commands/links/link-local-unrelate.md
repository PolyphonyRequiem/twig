---
command: link local unrelate
group: links
summary: Remove an existing Twig-local Related link without provider access.
stability: stable
mutates: local
---

# `twig link local unrelate`

Tombstone a symmetric Related edge in Twig's durable local relationship store:

```
twig link local unrelate <source-local-guid> <target-local-guid> [-o human|json|minimal]
```

## Behavior

The arguments must be canonical lowercase local GUIDv7 identities. Removal
uses only the local relationship store; it does not resolve a provider, check
the current provider's capability declaration, contact a provider, or sync
either endpoint. This keeps an existing Twig-local edge removable after a
connection change, including when the new provider declares Related native or
unknown.

The edge's local tombstone preserves its revision history. It does not remove
either work item or alter any provider relationship.

## Examples

Remove a local association:

```
twig link local unrelate 019a0000-0000-7000-8000-000000000001 019a0000-0000-7000-8000-000000000002
```

Request a structured result:

```
twig link local unrelate 019a0000-0000-7000-8000-000000000001 019a0000-0000-7000-8000-000000000002 -o json
```

An absent edge, an edge owned by a different storage route, or a concurrent
write conflict is reported as an error. See [`link local list`](./link-local-list.md).

## Exit codes

- `0` — the edge was tombstoned.
- `1` — no such edge exists, it exists under a different storage route, it
  changed concurrently, or the local store failed. Nothing is changed.
- `2` — an identity is not a canonical lowercase GUIDv7, or the output format is
  not `human`, `json`, or `minimal`.
