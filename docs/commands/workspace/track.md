---
command: workspace track
group: workspace
summary: Retain an explicit connection-qualified item pin in the selected shared Bench.
stability: stable
mutates: local
---

# `twig workspace track`

```sh
twig workspace track 12345 -o json
```

## Behavior

Adds a single-item selector to the existing selected user-wide Bench.
Uses an explicit numeric target or retained local seed alias, never a title
guess. An uncached published target can require provider acquisition.
Single and subtree selectors are independent; adding one preserves the other.

`--expect-bench` checks the captured canonical Bench GUID.
Old `--expect-binding`, `--expect-identity`, and `--expect-settings` capture
envelopes currently refuse rather than supplying a legacy browser route.

## Effects

Persists a shared local selector under snapshot/CAS checks and can cache acquired
observations. Does not publish/assign work or freeze a query's current results.
Cached views remain cached until an explicit pull.

## Failure modes

Missing selection, invalid/ambiguous target, changed Bench or unqualified capture
fails without retargeting. Protected drafts are not discarded.
