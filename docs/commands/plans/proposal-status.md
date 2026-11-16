---
command: proposal status
group: plans
summary: Inspect exact Core proposal journal rows and receipts without replay.
stability: stable
mutates: none
---

# `twig proposal status`

```sh
twig proposal status --file change.json -o json
```

## Behavior

Resolves the file's canonical digest in its immutable workspace connection.
Returns `found`, `allVerified`, and ordered operation identities, statuses,
attempt UUIDs and receipts, including published identity/revision/error evidence.
Missing journals report `found: false` and exit nonzero; incomplete ordinals refuse.

## Effects

Inspection only: no work HTTP, apply, settlement, draft mutation or replay.
Native `connection writes` sidecars are correlated evidence, not this journal's
execution authority.

## Failure modes

Missing/invalid file, different connection, unavailable store, missing journal
or inconsistent rows fail. Human, JSON aliases and minimal are supported,
not `ids`. Applied/acknowledged does not mean Verified.
