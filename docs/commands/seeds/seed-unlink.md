---
command: seed unlink
group: seeds
summary: Remove exact retained local creation-link intent.
stability: stable
mutates: local
---

# `twig seed unlink`

## Behavior

Removes the explicitly named retained seed parent/creation link through the same
qualified capture path as [`seed link`](seed-link.md).
It is not a provider relationship deletion.

## Effects

Local draft CAS; schema/endpoint acquisition can occur. No native mutation.

## Failure modes

Absent exact link, invalid endpoints, protected creation or changed evidence
fails. GitHub creation-link editing is not qualified.
