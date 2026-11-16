---
command: seed discard
group: seeds
summary: Discard eligible explicit unpublished seed intent.
stability: stable
mutates: local
---

# `twig seed discard`

## Behavior

Takes an actual negative seed alias. Requires interactive confirmation or explicit
`--yes` when confirmation is unavailable. Uses exact Core draft/store evidence;
it is not deletion of a published work item.

## Effects

Removes eligible local unpublished intent under dependency and journal protection.
Does not delete original acknowledged publication evidence or provider items.

## Failure modes

Absent/ambiguous seed, protected attempted creation, dependent intent or changed
store refuses. Do not discard an unknown creation to make another publish possible.
