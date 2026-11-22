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
twig connection list -o json
twig connection list --provider ado --org contoso --project MyProject -o json
twig connection writes -o json
```

## Behavior

Status reports selected or pinned **recorded** native identity/target evidence,
not fresh authenticated work access. `auth identities` and `connection list`
inspect registered metadata without changing selection. `connection list` lists
all device-local provider connections and this user's credential bindings through
the server, including unbound registrations. It does not infer a filter from the
workspace and does not read repository configuration. You may filter by provider
alone; dimension filters require complete explicit provider dimensions. An empty
inventory emits an empty JSON array.

Before fresh initialization, `connection status` resolves the portable requirement
in `twig.json` against local registrations. Missing registrations or credential
bindings give setup guidance without falling back to another user or connection.
Readiness is recorded metadata, not proof of authenticated provider access.

`auth status --provider github` inspects the explicitly selected GitHub App
credential only; it does not verify the pinned `gh` actor, refresh credentials,
or contact GitHub. Missing selection refuses.

`connection writes` inspects correlated original native transport evidence
without work HTTP. GitHub sidecars are bounded and identity/hash-checked against
their original store, connection, digest, ordinal and attempt. Missing/invalid
evidence stays unknown/refused. Only [`plan status`](../plans/plan-status.md)
reports Core execution authority.

## Effects

Inspection only: no settlement, replay, account switch, UUID/digest replacement,
evidence deletion or fence clearing. No tokens/PATs are printed.

## Failure modes

Unavailable/invalid selection, layout or evidence refuses. A layout-marker error
does not prove a missing identity. Migration/check/pin/unpin/default-transition/
reconcile-write routes are retired; status never repairs or reinitializes state.
Use an explicit refreshed read to exercise actual provider access.
