---
command: workspace area list
group: workspace
summary: Read shared current-connection area rule conditions and exact/under semantics.
stability: stable
mutates: none
---

# `twig workspace area list`

## Behavior

Lists retained area conditions with their one-based rule index, value and
exact/under mode. Includes captured Bench identity/revision.
Cached inspection, not provider discovery or a replacement configuration list.

## Effects

No work HTTP or rule/item mutation.

## Failure modes

Missing selection, unsupported provider or noncanonical retained rule refuses.
Refresh metadata separately with `bench planning refresh`.
