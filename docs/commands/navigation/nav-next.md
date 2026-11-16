---
command: nav next
group: navigation
summary: Select the next sibling in the retained numeric ordering.
stability: stable
mutates: local
---

# `twig nav next`

## Behavior

Human-only qualified hierarchy navigation. Selects the next sibling in numeric
ID/alias order, wrapping when appropriate; it is not backlog-priority ordering.

## Effects

Can acquire missing observations and writes local active context.
No assignment or claim. Structured output and GitHub navigation refuse.

## Failure modes

Missing/ambiguous hierarchy, unavailable source or no sibling fails.
See [navigation](README.md).
