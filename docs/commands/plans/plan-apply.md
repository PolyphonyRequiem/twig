---
command: plan apply
group: plans
summary: Deprecated alias for exact-file, exact-digest Core proposal application.
stability: stable
mutates: ado
---

# `twig plan apply`

## Behavior

Deprecated alias for [`proposal apply`](proposal-apply.md), with the same
explicit file and exact `--confirm` digest. Old plan schemas are not accepted.

## Effects

Can publish immediately through Core with durable journal/verification.
No blind retry, conflict overwrite or distributed transaction guarantee.

## Failure modes

The canonical apply gates and unresolved-effect behavior apply unchanged.
