---
command: state
group: work-items
summary: Apply one discovered transition without bypassing coupled required fields.
stability: stable
mutates: ado
---

# `twig state`

```sh
twig state <discovered-state> --id 12345 -o json
```

## Behavior

Resolves an exact state, then an unambiguous prefix, then a uniquely matched
supported category from the actual type. Already-in-state is an explicit no-op.
Published targets use one revision-bound Core Batch transition, not automatic
multi-hop chaining. A local seed state edit stays local.

Single-item machine calls require `--id`; human calls can use active context.
A state-only change cannot satisfy missing close-gate fields: use one
[`batch`](batch.md) or explicit proposal operation carrying the transition and
all coupled fields together.

## Effects

Can publish immediately through Core with journal/verification.
No implicit conflict retry or unrelated draft flush.

## Failure modes

Missing/ambiguous state, invalid transition, unsatisfied requirements, changed
evidence or unresolved effects fails. Never assume a process has a state named Done.
