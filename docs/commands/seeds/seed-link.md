---
command: seed link
group: seeds
summary: Capture a qualified local seed creation relationship.
stability: stable
mutates: local
---

# `twig seed link`

## Behavior

Captures explicit creation-link/parent intent using discovered hierarchy,
dependency or association metadata. At least one endpoint must be an eligible
local seed. Use `seed link --help` for endpoint/type spelling and direction.
GitHub creation links are not qualified.

## Effects

CAS local draft intent; can acquire endpoint/schema evidence. Does not acknowledge
or publish a native relationship, and is distinct from `link local related`.

## Failure modes

Absent/ambiguous endpoints, unsupported/duplicate links, attempted protected UUIDs
or changed store evidence refuse. Publication later rechecks the full plan.
