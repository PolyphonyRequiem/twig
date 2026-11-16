---
command: nav up
group: navigation
summary: Select the active item's unique discovered parent.
stability: stable
mutates: local
---

# `twig nav up`

## Behavior

Human-only ADO navigation through the unique discovered hierarchy relation.
Observes incomplete native relationships as needed and selects one unambiguous
parent. A local seed's captured parent intent can participate.

## Effects

Can read provider data/cache observations and writes the local active pointer.
Never assigns or claims work. Structured output and GitHub navigation refuse.

## Failure modes

Missing active item, multiple/absent parents, incomplete hierarchy evidence or
changed admission fails without guessing. See [navigation](README.md).
