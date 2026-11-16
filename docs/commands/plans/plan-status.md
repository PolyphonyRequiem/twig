---
command: plan status
group: plans
summary: Deprecated alias for exact Core proposal journal inspection.
stability: stable
mutates: none
---

# `twig plan status`

## Behavior

Deprecated alias for [`proposal status`](proposal-status.md).
Requires the original canonical file and connection; returns exact attempts and
receipts, not guessed legacy status.

## Effects

Inspection only, no work HTTP, settlement or replay.

## Failure modes

Missing/inconsistent journal or changed scope refuses.
