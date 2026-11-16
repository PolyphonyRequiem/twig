---
command: workspace sprint remove
group: workspace
summary: Remove an exact retained iteration expression from one shared rule.
stability: stable
mutates: local
---

# `twig workspace sprint remove`

## Behavior

Removes the named iteration expression from one selected current-connection
planning rule. Multiple rules require `--rule` plus captured
`--expect-revision`. It does not remove all rule intent or rewrite old split
configuration.

## Effects

Shared local rule CAS only; preserves other selectors and upstream items.

## Failure modes

Absent expression, invalid scope, ambiguity or changed revision/admission refuses.
See [planning](../bench/planning.md).
