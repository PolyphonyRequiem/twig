---
command: seed reconcile
group: seeds
summary: Settle eligible local seed evidence from original verified publication, without replay.
stability: stable
mutates: local
---

# `twig seed reconcile`

## Behavior

Uses original Core publication evidence/mappings to settle eligible local seed
state. If a create was verified but its Bench update was interrupted, run
`twig seed reconcile` in the original workspace. It uses the original receipt
and mapping to replace any remaining explicit seed memberships with provider
pins, even if the local draft has already been settled. It does not infer an
identity from the item's title or add it to a Bench it no longer belongs to.

## Effects

Can settle local evidence only under original verified outcomes. Never blindly
redispatches Create or treats an unknown attempt as failed.

## Failure modes

Missing, inconsistent, protected or unresolved original evidence refuses.
Inspect the original plan journal; do not change UUID/digest to bypass a fence.
