---
command: workspace track-tree
group: workspace
summary: Retain a discovered-hierarchy subtree selector in the selected Bench.
stability: stable
mutates: local
---

# `twig workspace track-tree`

```sh
twig workspace track-tree 12345 -o json
```

## Behavior

Adds a subtree selector rather than pre-expanding descendants into individual
pins. Requires unique actual directed hierarchy metadata. New descendant
observations participate on later pulls, not through an implicit live request
on every cached render. GitHub subtree selectors are not qualified.

Single/subtree pins coexist. Target/admission/capture rules match
[`workspace track`](track.md).

## Effects

Shared selector CAS and possible provider/schema reads; no provider mutations.

## Failure modes

Unavailable target/hierarchy, missing selection, changed Bench or unsupported
capture refuses without degrading the selector to a single pin.
