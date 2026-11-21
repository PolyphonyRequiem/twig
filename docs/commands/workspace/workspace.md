---
command: workspace
group: workspace
summary: Read an explicitly named Bench, optionally refreshing observations without saving drafts.
stability: stable
mutates: local
---

# `twig workspace --bench <name-or-id>`

```sh
twig workspace --bench MyWork
twig workspace --bench MyWork --refresh
```

## Behavior

Shows the named user-wide Bench's cached connection-scoped membership,
protected drafts and unavailable-source information. It is not an automatic
"assigned to me in the current sprint" query.
Human and machine output have the same scope; neither adds a viewer's focus.

`--refresh` explicitly pulls permitted Bench scope without saving drafts.
It does not replace shared rules with result IDs. GitHub sync is not qualified.

## Effects

The default is cached inspection. Explicit refresh performs provider reads and
observation writes, never seed publication or a pending-draft flush.

## Failure modes

Missing Bench, absent admission or incomplete scope fails rather than inventing a
default or complete empty membership. Old `--all`, `--tree`, `--live` and captured
UI envelopes are retired.
