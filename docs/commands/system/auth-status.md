---
command: auth status
group: system
summary: Inspect recorded identity/binding selection and native transport evidence without work HTTP.
stability: stable
mutates: none
---

# Identity, connection status, and write evidence

```sh
twig auth status -o json
twig connection status -o json
twig auth identities -o json
twig connection list --org contoso --project MyProject -o json
twig connection writes -o json
```

## Behavior

Status reports selected or pinned **recorded** native identity/target evidence,
not fresh authenticated work access. `auth identities` and `connection list`
inspect registered metadata without changing selection. Paired explicit
organization/project coordinates are required when provided.

`auth status --provider github` inspects the explicitly selected GitHub App
credential only; it does not verify the pinned `gh` actor, refresh credentials,
or contact GitHub. Missing selection refuses.

`connection writes` inspects correlated original native transport evidence
without work HTTP. GitHub sidecars are bounded and identity/hash-checked against
their original store, connection, digest, ordinal and attempt. Missing/invalid
evidence stays unknown/refused. Only [`proposal status`](../plans/proposal-status.md)
reports Core execution authority.

## Effects

Inspection only: no settlement, replay, account switch, UUID/digest replacement,
evidence deletion or fence clearing. No tokens/PATs are printed.

## Failure modes

Unavailable/invalid selection, layout or evidence refuses. A layout-marker error
does not prove a missing identity. Migration/check/pin/unpin/default-transition/
reconcile-write routes are retired; status never repairs or reinitializes state.
Use an explicit refreshed read to exercise actual provider access.
