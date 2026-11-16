---
command: discard
group: work-items
summary: Remove eligible existing-item drafts without deleting journal or provider work.
stability: stable
mutates: local
---

# `twig discard`

## Behavior

Requires one explicit positive `--id` or `--all`.
Interactive confirmation is required unless `--yes` is explicit; redirected/
structured callers cannot rely on a prompt. Seeds use `seed discard`.

## Effects

CAS-removes eligible local existing-item field drafts only. Does not change
provider items, remove seeds, delete original journal evidence or clear unknown
write fences.

## Failure modes

Invalid scope, missing confirmation, protected intent or changed store evidence
refuses. Discarding local data is never proof an uncertain remote write failed.
