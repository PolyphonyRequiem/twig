---
command: auth login
group: system
summary: Explicitly enroll or renew a named native identity without switching worktree selection.
stability: stable
mutates: local
---

# Enrollment and binding

```sh
twig auth login --identity work
twig auth pat --identity work-pat --org contoso
twig connection bind --org contoso --project MyProject --identity work --default
```

## Behavior

AAD `auth login` uses loopback PKCE; `--device-code` is tenant-policy-dependent.
`--tenant` selects the tenant; `--no-browser` suppresses browser opening for
loopback sign-in. Without an explicit alias, renewal must resolve the currently
attached identity; no Azure CLI, machine-cache, or last-login fallback exists.

`auth pat` reads a non-echoing prompt or one line with `--stdin`. New enrollment
requires explicit identity/organization. There is no secret argument.
Native principal attestation, not the alias/display name, establishes identity.

`connection bind` joins an already enrolled identity to explicit ADO coordinates.
Without `--default` it does not select a default. A different existing default
refuses; fresh `init --binding <id>` can name an existing matching binding.

`auth login --provider github --identity <alias> --client-id <app-client-id>`
is separate GitHub App device enrollment, not `gh` login or the normal fresh
GitHub workspace's exact-account selection. Read the
[authentication guide](../../features/authentication.md) before choosing a route.

## Effects

Explicit authentication/network/browser effects and private credential/registry
writes. Enrollment does not silently rebind another workspace or publish work
items. Credentials never belong in arguments, output, or telemetry.

## Failure modes

Missing authority, principal mismatch, denied grants, malformed/uncertain issuance
or private-storage failure refuses rather than selecting another credential.
`auth logout/clear` and identity hot-switch routes are retired.
