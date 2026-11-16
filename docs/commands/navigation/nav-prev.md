---
command: nav prev
group: navigation
summary: Select the previous sibling in the retained numeric ordering.
stability: stable
mutates: local
---

# `twig nav prev`

## Behavior

Human-only qualified hierarchy navigation. Selects the previous sibling in
numeric ID/alias order, wrapping when appropriate, not backlog-priority order.

## Effects

Can acquire missing observations and writes local active context.
No assignment or claim. Structured output and GitHub navigation refuse.

## Failure modes

Missing/ambiguous hierarchy, unavailable source or no sibling fails.
See [navigation](README.md).
