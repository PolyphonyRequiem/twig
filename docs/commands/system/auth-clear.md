---
command: auth clear
group: system
summary: Invalidate selected access/admission proof while preserving credentials and bindings.
stability: stable
mutates: local
---

# `twig auth clear`

Clears the current attached binding's access/admission proof, or an explicitly
named identity's proof before attachment. This is integration-branch behavior,
not a partial release or global credential deletion.

## Synopsis

```text
twig auth clear [--identity <alias>] [-o <format>]
```

## Behavior

Without `--identity`, the command resolves the current attached binding. With
`--identity`, it targets that registered identity explicitly and works outside
attachment. It leaves stored credentials, binding selection, pending work, read
data, other identities and legacy credentials unchanged.

For AAD, clearing removes the selected access cache; the refresh credential remains.
File-cache deletion uses the existing best-effort store behavior and is not proof
that a locked file was removed. For PAT, clearing advances only that credential's
admission-reset stamp; already-open runtimes must re-attest the unchanged PAT before
their next work request. It does not erase the PAT or select another principal.

The next work request uses the same registered identity. It does not bootstrap
from Azure CLI, a machine cache or the last login. Repair with `auth login
--identity <alias>` for AAD or `auth pat --identity <alias>` for PAT. A wrong
principal cannot replace the existing credential under the same alias.

Help remains available without attachment. Output reports selected proof/cache
invalidation without secret contents.

## Exit codes and failure modes

Successful invalidation returns `0`, including an absent access-cache file.
An unnamed clear requires a valid attached selection; a named clear requires a
registered alias. Resolution or PAT reset-write failures return `1` with formatted
diagnostics, before any work HTTP. Cancellation is honored before invalidation.

## See also

- [`auth status`](auth-status.md) — inspect the effective attached binding.
- [`auth login`](auth-login.md) — enroll or renew an explicitly selected identity.
