---
command: auth login
group: system
summary: Enroll an explicit AAD identity or GitHub App user identity without selecting another connection.
stability: stable
mutates: local
---

# `twig auth login` / `twig auth pat`

Enrolls an AAD identity through default `auth login`, a GitHub App user identity
through `auth login --provider github`, or an ADO Personal Access Token identity
through `auth pat`. For ADO, the central system registry stores method-specific principal
metadata and an opaque credential reference; private files store secrets separately.
This integration-branch change is not a partial-feature release.

## Synopsis

```text
twig auth login --identity <alias> [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
twig auth login [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
twig auth login --provider github --identity <alias> --client-id <registered-app-client-id> [-o <format>]
twig auth logout --provider github --identity <alias> [--client-id <registered-app-client-id>] [-o <format>]
twig auth pat --identity <alias> --org <organization-or-https-endpoint> [--stdin] [-o <format>]
twig auth pat [--stdin] [-o <format>]
```

## Behavior

For ADO, `--identity` selects an explicit management alias and works before worktree
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

### GitHub App OAuth enrollment

`--provider github` explicitly selects Twig's GitHub App device flow on
**github.com**. It does not use a PAT, GitHub CLI credentials, or ADO's app
registration. `--tenant` is incompatible; device flow is always used and no
browser is launched. The public registered GitHub App client ID is required via
`--client-id` or user-local `github.appclientid`. The explicit identity alias comes
from `--identity` or user-local `github.oauthidentity`. There is no built-in
GitHub App registration or client secret. The older standalone OAuth App protocol
helper is not the registration used by this command.

Device instructions (verification URL and short user code only) are sent to
stderr; completion emits one human/JSON/minimal result on stdout, never a token.
The flow honors `authorization_pending`, server polling intervals and
`slow_down`, cancellation, a 15-second per-request deadline and at most
15 minutes for the device session. Redirects, malformed/oversized responses,
non-expiring grants and broader OAuth scopes refuse enrollment. The
`offline_access` authorization parameter requests expiring user tokens, **not**
repository permissions.

Enrollment observes `GET /user` at the fixed GitHub API origin, and records the
immutable user node ID with the host and app client ID. Login names are display
locators, not identity authority. Reusing the same alias/app selection with a
different actor refuses without replacing the existing identity.

Access and refresh tokens are stored together beneath
`<Twig system-state home>/github-oauth/` (`TWIG_USER_HOME` is honored as for ADO).
The file name hashes host, public app client ID and alias; the private envelope
contains and checks that selection plus the immutable actor. This is
file-backed private storage, **not** an OS credential vault: Windows files have
protected owner-only ACLs; Unix files are created with mode `0600`. Temporary
replacements are private before secret bytes are written, flushed and atomically
renamed. No token material belongs in configuration, the registry, worktree files,
proposal journals, telemetry, command arguments or output.

Refresh uses the same registered public client ID and no client secret, as
supported specifically for GitHub App tokens issued through device flow.
GitHub's documented defaults are an eight-hour access token and six-month
refresh token; Twig uses the response expiry, not a perpetual token assumption.
The selected identity's cross-process file lease serializes login, logout and
rotation. Before a one-use refresh leaves the process, Twig durably marks the
pair as refresh-pending. An interrupted, refused or ambiguous rotation remains
`needs-login` and is never replayed; a rotated pair is saved before its access
token is exposed. Actor mismatch refuses use. A new runtime reattests cached
access-token material before admitting it to read requests.

Login does not select a workspace credential mechanism. Explicit normal-read
selection is separate:

```text
twig config github.appclientid <registered-app-client-id>
twig config github.oauthidentity personal
twig config github.credentialsource github-app
```

The absent credential-source preference still means `gh`; `github-app` never
falls back to `gh`, environment tokens, PATs or another alias. Missing credentials
require sign-in. Enterprise hosts are not supported by this initial app-user flow.
An immutable repository-target binding is still independently required.
**GitHub App credentials do not enable native writes.** Write admission,
installation/repository qualification and relationship permissions are separate.

`auth logout --provider github` removes only the selected local token pair and
local admission cache; siblings and repository installations are unchanged.
It **does not revoke** remote authorization. To revoke, use GitHub **Settings >
Applications > Authorized GitHub Apps**; uninstall separately to remove an
installation. Twig does not pretend a local logout remotely revoked a token.

#### Registration (administrator action, not performed by Twig)

Register a **GitHub App**, proposed name **Twig Work Items** (names must be
globally unique), with homepage `https://github.com/PolyphonyRequiem/twig`.
Use no callback for device-only sign-in. Enable **Device Flow** and **Expire user
authorization tokens**; leave **Request user authorization during installation**
off, webhooks inactive, and all permissions except **Issues: read/write** and
baseline **Metadata: read** at **No access**. Initially allow installation
**Only on this account**; installation and selection of repositories require a
separate administrator decision. No private key or client secret is needed for
this user-token device flow. A selected-repository installation limits access
to the intersection of app, installation and user's access; token format or an
empty OAuth `scope` string alone proves none of those grants.

See GitHub's [device grant documentation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
and [device-issued token refresh exception](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens).

### ADO PAT verification and bindings

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
