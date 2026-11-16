---
command: workspace area remove
group: workspace
summary: Remove an exact area condition from one shared native planning rule.
stability: stable
mutates: local
---

# `twig workspace area remove`

## Behavior

Removes the case-insensitively matched path from one selected current-connection
planning rule. Multiple rules require explicit `--rule` plus
`--expect-revision`. Other conditions/pins remain, not a global config-array
rewrite.

## Effects

Shared local rule CAS only, no provider item mutation.

## Failure modes

Absent path, ambiguity, invalid shape or changed admission/revision refuses.
See [planning](../bench/planning.md).
