---
command: plan preview
group: plans
summary: Deprecated alias for current Core proposal preview.
stability: stable
mutates: local
---

# `twig plan preview`

Prefer [`proposal preview`](proposal-preview.md).

## Behavior

Uses the same file-scoped Core preview and exact digest, not a legacy schema,
interactive presenter, or old captured UI envelope.

## Effects

Can retain local journal intent and acquire current evidence, never publishes.

## Failure modes

The same input, admission and blocker rules as canonical preview.
