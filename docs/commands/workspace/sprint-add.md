---
command: workspace sprint add
group: workspace
summary: Add iteration intent to one shared native planning rule.
stability: stable
mutates: local
---

# `twig workspace sprint add`

```sh
twig bench planning refresh
twig workspace sprint add "@Current"
```

## Behavior

Adds an absolute native iteration path or `@Current`, `@Current+N`,
`@Current-N` expression. Relative forms require the actual selected team's
cached calendar and retain that team identity.
Edits one current-connection shared planning rule, not `twig.json` sprint arrays.
Multiple rules require `--rule` plus captured `--expect-revision`.

## Effects

Shared local intent CAS under native admission, no work-item publication.
Relative expressions resolve during explicit pull, not an implicit live refresh.

## Failure modes

Invalid expression, missing/different cached team, ambiguous or noncanonical
rule, changed revision or unsupported provider refuses.
See [planning](../bench/planning.md).
