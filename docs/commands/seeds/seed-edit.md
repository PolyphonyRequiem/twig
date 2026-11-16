---
command: seed edit
group: seeds
summary: Edit an eligible local seed through its acquired native field schema.
stability: stable
mutates: local
---

# `twig seed edit`

## Behavior

Takes an actual unpublished negative seed ID and opens the retained external
editor. Only discovered editable fields are accepted; unknown, duplicate,
read-only or invalid typed sections refuse. Cancellation does not stage changes.
GitHub seed editing through this editor is not qualified.

## Effects

Can acquire schema and CAS local fields. No provider creation/publication.

## Failure modes

An attempted/mapped/protected original creation UUID cannot be edited into a new
attempt. Missing/ambiguous aliases, unsupported field types or changed store
evidence fail without overwriting captured intent.
