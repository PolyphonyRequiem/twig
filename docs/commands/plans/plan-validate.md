---
command: plan validate
group: plans
summary: Deprecated alias for canonical Core proposal format validation.
stability: stable
mutates: none
---

# `twig plan validate`

## Behavior

Deprecated alias for [`proposal validate`](proposal-validate.md).
Format/digest inspection only, not current application/policy evidence.

## Effects

No provider request or publication.

## Failure modes

Missing/invalid canonical file refuses; old formats are not translated.
