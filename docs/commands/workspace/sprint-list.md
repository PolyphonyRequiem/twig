---
command: workspace sprint list
group: workspace
summary: Read retained iteration expressions and their exact rule/team scope.
stability: stable
mutates: none
---

# `twig workspace sprint list`

## Behavior

Lists retained iteration expressions with one-based rule index and team/absolute
mode. Includes captured Bench identity/revision.
This is cached intent, not the current provider iteration membership.

## Effects

No work HTTP, rule replacement, item mutation or selection change.

## Failure modes

Missing selection/admission or noncanonical rule refuses.
See [planning](../bench/planning.md).
