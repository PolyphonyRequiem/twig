---
command: states
group: process
summary: Human active-item state convenience; machine callers name a type.
stability: stable
mutates: none
---

# `twig states`

## Behavior

Human-only convenience using the selected item's actual discovered type.
Structured callers must use [`process <type>`](process-type.md), not active context.

## Effects

Can acquire schema; no mutation or selection change.

## Failure modes

Missing active item, unavailable schema or structured invocation refuses.
