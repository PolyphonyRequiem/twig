---
command: seed view
group: seeds
summary: Inspect retained UUID-backed local seeds.
stability: stable
mutates: none
---

# `twig seed view`

## Behavior

Lists unpublished Core drafts with their actual negative aliases, canonical
UUIDs, types, titles and typed fields. It is not the old parent-grouped cache
dashboard and does not infer a future provider ID.

## Effects

Snapshot inspection only, no creation or active selection.

## Failure modes

Unavailable store or ambiguous retained aliases refuses instead of rebinding.
Use `show --local-id <uuid> -o json` for an exact original identity.
