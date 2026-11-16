---
command: seed links
group: seeds
summary: List retained local creation-link intent without publication.
stability: stable
mutates: none
---

# `twig seed links`

## Behavior

Lists explicit retained parent/link intent, optionally for a selected alias.
Direction and kind come from the captured native relation, not assumed endpoint
roles. These rows are unpublished intent, not provider acknowledgements.
GitHub creation links are not qualified.

## Effects

Local snapshot inspection only.

## Failure modes

Unadmitted store or ambiguous aliases refuse without inventing identities.
