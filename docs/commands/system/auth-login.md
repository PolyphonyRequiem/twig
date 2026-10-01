---
command: auth login
group: system
summary: Enroll or renew an explicitly named AAD identity without changing another connection's account.
stability: stable
mutates: local
---

# `twig auth login`

Enrolls an Azure Active Directory (AAD) identity through interactive sign-in. The
central system registry stores principal metadata and an opaque credential
reference; user-private credential files store refresh tokens separately.
This integration-branch change is not a partial-feature release.

## Synopsis

```text
twig auth login --identity <alias> [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
twig auth login [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
```

## Behavior

`--identity` selects an explicit management alias and works before worktree
attachment. Without it, login resolves the current attached binding and renews
that identity. Missing attachment or selection refuses; it never chooses the
Azure CLI active account, a machine cache, or the last logged-in identity.

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
Output includes safe principal metadata, never access or refresh tokens.

## Exit codes and failure modes

Successful enrollment returns `0`. Interactive, principal-validation and storage
failures return `1` with repair guidance. Cancellation propagates through the
interactive flow.

Successful initial binding also returns `0`. An unknown identity, unpaired
endpoint coordinates, or a different existing default refuses with setup
guidance. No binding refusal selects a fallback account.

## See also

- [`auth status`](auth-status.md) — inspect the effective attached binding.
- [`auth clear`](auth-clear.md) — invalidate only the selected access cache.
