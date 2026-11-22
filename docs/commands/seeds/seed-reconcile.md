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
state. It is not a repair of legacy `seed_links` rows or permission to infer a
new remote item from its title.

## Effects

Can settle local evidence only under original verified outcomes. Never blindly
redispatches Create or treats an unknown attempt as failed.

## Failure modes

Missing, inconsistent, protected or unresolved original evidence refuses.
Inspect the original plan journal; do not change UUID/digest to bypass a fence.
