---
command: process <type>
group: process
summary: Describe one discovered Core type and its relevant schema evidence.
stability: stable
mutates: none
---

# `twig process <type>`

## Behavior

Selects an actual discovered type reference (or qualified supported native name)
and reports fields, states, transitions/rules and gaps. Unknown/ambiguous types
fail; nothing defaults to Task, Bug, or another process-specific name.
Use this route to inspect states for a specific type.

## Effects

Can acquire provider schema. No work-item mutation or context selection.

## Failure modes

Unknown type, denied discovery, incomplete relevant evidence or changed native
admission fails. Requirements are evidence, not transition authorization.
