---
command: auth login
group: system
summary: Enroll or renew an explicitly named AAD identity without changing another connection's account.
stability: stable
mutates: local
---

# `twig auth login` / `twig auth pat`

Enrolls an AAD identity through `auth login`, or a Personal Access Token identity
through `auth pat`. The central system registry stores method-specific principal
metadata and an opaque credential reference; private files store secrets separately.
This integration-branch change is not a partial-feature release.

## Synopsis

```text
twig auth login --identity <alias> [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
twig auth login [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
twig auth pat --identity <alias> --org <organization-or-https-endpoint> [--stdin] [-o <format>]
twig auth pat [--stdin] [-o <format>]
```

## Behavior

`--identity` selects an explicit management alias and works before worktree
attachment. Without it, login resolves the current attached binding and renews
that identity. Missing attachment or selection refuses; it never chooses the
Azure CLI active account, a machine cache, or the last logged-in identity.


### AAD enrollment
The default flow uses a loopback listener and Proof Key for Code Exchange (PKCE).
`--device-code` uses the device authorization grant; enterprise policy can block
that grant. `--tenant` targets a tenant; `--no-browser` prints the authorization
URL instead of opening it for the loopback flow.

Enrollment exchanges the refresh credential for an ADO access token, validates
its audience and tenant/object/issuer evidence, and registers that principal.
Re-enrollment under an existing alias must match its principal and authority.
A mismatch leaves the existing credential intact. JWT decoding is consistency
checking, not local signature verification; enrollment relies on authenticated
issuance and ADO validates credentials presented to it.

### PAT enrollment and repair

`auth pat` reads a token from a non-echoing prompt, or one line of standard input
with `--stdin` or redirected input. There is no secret argument. New enrollment
outside attachment requires `--identity` and `--org`; an existing named PAT can
reuse its registered authority. Omitting `--identity` renews the current attached
PAT binding and refuses an AAD binding rather than converting it.

Before storing a PAT, a read-only `/_apis/connectionData` probe at the HTTPS
organization authority establishes `authenticatedUser.id`. Display names are
labels, not evidence of authority. Reusing an alias requires the same method,
authority and authoritative ADO principal. A different principal—even with the
same display name—leaves the saved credential unchanged.

Attached work requests use only the selected credential reference. Admission proof
is tied to the exact PAT material, authority and registered principal; replacing
bytes at the same reference forces fresh attestation before work HTTP, including
on an already-open runtime. Missing/malformed principal evidence or an unsuccessful
probe fails closed without an alternate-account fallback. Verified same-principal
renewal preserves selection revisions, pending work and read data, and subsequent
requests use the repaired material without reconnecting.

Legacy `TWIG_PAT`, auth-method environment settings and raw configuration PATs do
not override an attached binding. Clear only the selected access/admission proof
with `auth clear`; this does not delete its PAT or another identity's credentials.

Login does not select an endpoint default or switch an existing binding. To
create an initial selection, use:

```text
twig connection bind --org <organization> --project <project> --identity <alias> --default
```

A different existing default refuses; safe identity transitions belong to the
separate guarded transition workflow. Normal work requests require attachment.

`connection bind` does not perform interactive sign-in: `--identity` must name
an already registered identity. Explicit `--org` and `--project` must be paired;
omitting both uses the current portable endpoint. Without `--default`, creating
a binding does not select it implicitly. Binding administration can run before
attachment and never authorizes work requests from an unattached checkout.

## Storage and output

The metadata home is the existing system-state root unless `TWIG_USER_HOME`
explicitly supplies an absolute path. Blank or relative overrides refuse.
Credentials live under its private `credentials` directory, keyed by opaque
references; they are not written to portable configuration or registry rows.
PAT files and their temporary replacements are created user-private before secret
bytes are written (owner-only Windows ACLs or Unix mode `0600`). A per-credential
admission-reset stamp invalidates proof; it contains no secret or principal proof.
Output includes safe method-specific principal metadata, never access, refresh
tokens or PATs. This is file-backed private storage, not an OS credential vault.

## Exit codes and failure modes

Successful enrollment returns `0`. Interactive, principal-validation and storage
failures return `1` with repair guidance. Cancellation propagates through the
interactive flow.

PAT attestation and storage failures return `1` without admitting a work request.
Successful PAT enrollment does not attach a checkout, select a binding default or
authorize normal work outside an attachment.

Successful initial binding also returns `0`. An unknown identity, unpaired
endpoint coordinates, or a different existing default refuses with setup
guidance. No binding refusal selects a fallback account.

## See also

- [`auth status`](auth-status.md) — inspect the effective attached binding.
- [`auth clear`](auth-clear.md) — invalidate only the selected access cache.
