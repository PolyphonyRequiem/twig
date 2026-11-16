---
command: workspace area sync
group: workspace
summary: Refresh native planning metadata without replacing Bench rule intent.
stability: stable
mutates: local
---

# `twig workspace area sync`

## Behavior

Uses the same explicit metadata refresh as `bench planning refresh`: areas and
the exact selected team's iteration calendar. It no longer replaces saved area
filters with today's team paths and does not fetch work items.

## Effects

Provider metadata reads and local planning observation writes.
Shared rules, active selection, drafts and upstream items are unchanged.

## Failure modes

Unavailable context/team/planning evidence or changed admission refuses.
See [planning](../bench/planning.md).
