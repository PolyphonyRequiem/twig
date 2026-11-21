---
command: ws
group: workspace
summary: Alias for reading or refreshing an explicitly named Bench.
stability: stable
mutates: local
---

# `twig ws`

## Behavior

Use `twig ws --bench MyWork` to inspect cached membership. Add `--refresh` to
pull permitted members on supported providers, exactly as with [`workspace`](workspace.md).

## Effects

Refresh may update cached observations. Neither form saves drafts or publishes work items.
