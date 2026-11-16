---
command: seed validate
group: seeds
summary: Evaluate seed publication prerequisites without creating items.
stability: stable
mutates: none
---

# `twig seed validate`

## Behavior

Evaluates eligible local seeds against actual schema, captured creation
dependencies and declared Twig policy. Explicit selection uses the real returned
seed alias. Reports per-seed pass/failure and total counts.
Validation is not a publication acknowledgement or a lease on provider state.

## Effects

Can acquire schema/application evidence; never creates provider items.

## Failure modes

Missing scope/seed, unavailable requirements, dependency/rule or admission errors
refuse. Per-seed failures return nonzero even when other rows pass.
