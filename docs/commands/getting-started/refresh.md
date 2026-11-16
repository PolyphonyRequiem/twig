---
command: refresh
group: getting-started
summary: Deprecated alias for pull-only sync of the selected Core Bench.
stability: stable
mutates: local
---

# `twig refresh`

Prefer [`twig sync --pull-only`](sync.md).

## Behavior

Uses the same selected-Bench Core pull path without saving drafts or publishing
seeds. It is not a legacy cache refresh and is not GitHub-qualified.

## Effects

Provider reads and local observation writes, never a draft flush.

## Failure modes

The same selection, native admission, and incomplete-pull failures as pull-only sync.
