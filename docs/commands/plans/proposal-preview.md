---
command: proposal preview
group: plans
summary: Check current Core proposal evidence and retain review intent without publication.
stability: stable
mutates: local
---

# `twig proposal preview`

```sh
twig proposal preview --file change.json -o json
twig proposal preview --file change.json --expect-digest <digest> -o json
```

## Behavior

Reads the current canonical Core proposal, verifies its immutable connection,
checks current application evidence and retains the exact reviewed intent.
Reports the canonical `digest`, `canApply`, operation IDs/kinds, and available
blocking policy/process decisions. `canApply: false` is a blocker even if preview
itself exits successfully.

The current review model is bounded; it is **not the former complete v1/v2
before/after presentation envelope**. Inspect the proposal's full typed effects
as well as reported blockers. A preview is not a lease on remote state.
`--expect-digest` refuses changed canonical intent before retention.

`--interactive` and `--include-rendering` are retired; old OMP presentation
recipes do not restore them. Use the ordinary structured/plain path or the
separate native daemon host's review workflow.

## Effects

Can acquire provider/schema/application evidence and retain the local proposal
journal. Never publishes, flushes unrelated drafts, or authorizes another digest.

## Failure modes

Invalid file, changed digest/connection/store, admission or acquisition errors
fail. Inspect `canApply` and decisions, not only exit status.
Apply requires this exact file and digest.
