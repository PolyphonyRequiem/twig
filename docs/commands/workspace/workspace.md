---
command: workspace
group: workspace
summary: Read static selected Bench membership, or explicitly pull it without saving drafts.
stability: stable
mutates: local
---

# `twig workspace` / `twig ws`

## Behavior

Shows the selected user-wide Bench's cached connection-scoped membership,
protected drafts and unavailable-source information. It is not an automatic
"assigned to me in the current sprint" query.
Machine output excludes implicit active-item/relationship expansion.

`workspace sync` explicitly pulls permitted selected Bench scope without saving
drafts; unlike ordinary `sync`, it is read-only with respect to provider items.
It does not replace shared rules with result IDs. GitHub sync is not qualified.

## Effects

Default view is cached inspection. Explicit sync performs provider reads and
observation writes, never seed publication or a pending-draft flush.

## Failure modes

Missing selection/admission or incomplete scope fails rather than inventing a
default or complete empty membership. Old `--all`, `--tree`, `--live` and captured
UI envelopes are retired.
