---
command: workspace area
group: workspace
summary: Read the current Bench view over shared native planning rules.
stability: stable
mutates: none
---

# `twig workspace area`

## Behavior

Reads the static current Bench view. Areas are part of shared native planning
rules, not an old repository-local configuration array.
Use [`Bench planning`](../bench/planning.md) for explicit rule edits and metadata
refresh. GitHub planning parity is not qualified.

## Effects

Cached presentation, no work-item publication or implicit rule replacement.

## Failure modes

Missing selection/source/evidence refuses.
