---
command: pending
group: plans
summary: Inspect retained typed Core drafts without changing them.
stability: stable
mutates: none
---

# `twig pending`

```sh
twig pending -o json
```

## Behavior

Returns store version, draft/field counts, exact item identities, baseline
revisions, old-value presence, typed old/new values, and retained parent/resource/
creation-link intent. This is the Core draft snapshot, not legacy pending row IDs
or a serialized list of queued network requests.

## Effects

No draft/journal change, work HTTP, publication, or context selection.

## Failure modes

Unavailable/unadmitted store fails. Inspection supports human, JSON aliases and
minimal output, not `ids`. A draft list is not proof of provider delivery.
