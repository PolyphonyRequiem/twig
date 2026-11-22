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
twig connection add --provider ado --org contoso --project MyProject
twig connection bind --provider ado --org contoso --project MyProject --identity work
```

## Behavior

AAD `auth login` uses loopback PKCE; `--device-code` is tenant-policy-dependent.
`--tenant` selects the tenant; `--no-browser` suppresses browser opening for
loopback sign-in. Without an explicit alias, renewal must resolve the currently
attached identity; no Azure CLI, machine-cache, or last-login fallback exists.

`auth pat` reads a non-echoing prompt or one line with `--stdin`. New enrollment
requires explicit identity/organization. There is no secret argument.
Native principal attestation, not the alias/display name, establishes identity.

`connection add` registers provider dimensions through the local server without
selecting credentials. `connection bind` associates your credential with an
already registered connection. Both require explicit provider dimensions and work
outside a repository; neither reads or modifies `twig.json`. Binding a different
enrolled identity adds another candidate without changing an initialized workspace.
`connection list` includes both CLI and native setup. For ADO it shows the exact
organization, project and binding ID for each candidate; use the spellings in
your manifest when binding, even if the same organization has another URL form.

For GitHub, use `connection add --provider github --host github.com --repository owner/repo`,
then the same dimensions with `connection bind --account <gh-user>`. A GitHub App
binding instead uses `--identity <oauth-alias> --client-id <public-client-id>`.
Native connections use `connection add --provider twig --database <name>` and need
no credential binding. `--default` is no longer a connection-bind option.

After cloning a repository with `twig.json`, register its required dimensions and
bind **your own** credential on this device, then run `twig init` unchanged. The
manifest contains no credential selectors or local registration IDs.

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
