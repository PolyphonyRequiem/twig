---
command: auth clear
group: system
summary: Invalidate only the selected identity's access cache, preserving refresh credentials and bindings.
stability: stable
mutates: local
---

# `twig auth clear`

Clears the current attached binding's access cache. This integration-branch
behavior replaces ambient global credential deletion; it is not a partial release.

## Synopsis

```text
twig auth clear [-o <format>]
```

## Behavior

The command resolves the selected binding and invalidates its in-memory and
per-credential access caches. It leaves refresh credentials, binding selection,
registry metadata, other identities and legacy credentials unchanged. A missing
access-cache file is harmless. File-cache deletion uses the existing best-effort
store behavior; clearing is not proof that a locked file was removed.

The next work request renews through that same identity's verified refresh
credential. It does not bootstrap from Azure CLI, a machine cache, or the last
login. To repair credentials, use `twig auth login --identity <alias>`; an
account mismatch requires explicit enrollment, not another account fallback.

Normal resolution requires a valid attachment and explicit binding default.
Help remains available without attachment. The output reports selected-cache
invalidation without secret contents.

## Exit codes and failure modes

Successful invalidation returns `0`, including an absent access-cache file.
An unattached worktree or missing/invalid selection refuses before deletion or
work HTTP. Cancellation is honored before invalidation.

## See also

- [`auth status`](auth-status.md) — inspect the effective attached binding.
- [`auth login`](auth-login.md) — enroll or renew an explicitly selected identity.
