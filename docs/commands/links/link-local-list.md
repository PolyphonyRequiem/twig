---
command: link local list
group: links
summary: List Twig-local Related links touching a local work item.
stability: stable
mutates: none
---

# `twig link local list`

Read active local-only Related edges touching a canonical lowercase local
GUIDv7 identity:

```
twig link local list <local-guid> [-o human|json|minimal]
```

`-o ids` is not supported because this format emits numeric work-item IDs,
while these endpoints are local GUIDv7 identities.

This reads the durable Twig relationship store only. It does not resolve the
active provider, require a provider identity for the local item, or contact or
sync a provider. Each result has local GUID endpoints and is labelled
`origin: twig-local`, `storage: local-only`, and
`publication: not-applicable`. The edge is never merged into provider-native
relationships.

## Examples

List local relationships in human-readable form:

```
twig link local list 019a0000-0000-7000-8000-000000000001
```

Emit the local relationship array as JSON:

```
twig link local list 019a0000-0000-7000-8000-000000000001 -o json
```

With `-o minimal`, each edge is one tab-separated row containing source ID,
target ID, kind, origin, storage, and publication state. An item with no local
Related edges returns an empty list.
