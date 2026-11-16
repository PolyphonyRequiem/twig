---
command: nav down
group: navigation
summary: Select a discovered child by unambiguous ID or title pattern.
stability: stable
mutates: local
---

# `twig nav down`

```sh
twig nav down
twig nav down "authentication"
```

## Behavior

Human-only ADO child selection using the discovered hierarchy. A pattern can
match ID or title; multiple matches can prompt interactively.
Cancelled/ambiguous selection preserves context.

## Effects

Can acquire native observations, then changes local active context.
Does not assign or claim work. Structured output and GitHub navigation refuse.

## Failure modes

No unique selected child, unavailable hierarchy/source, or changed admission
fails without selecting an arbitrary match. See [navigation](README.md).
