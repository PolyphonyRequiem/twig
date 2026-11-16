---
command: area sync
group: workspace
summary: Deprecated alias for native planning metadata refresh.
stability: stable
mutates: local
---

# `twig area sync`

## Behavior

Deprecated alias for [`workspace area sync`](area-sync.md).
Refreshes native planning metadata; never replaces shared area-rule intent.

## Effects

Provider metadata reads and local observation writes, no upstream item changes.
