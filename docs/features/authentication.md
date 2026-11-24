# Authentication

Twig uses explicit connection identity, not whichever account last signed in.
Status describes recorded selection; a successful status is **not proof of fresh
provider access**.

## Azure DevOps

Enroll a named AAD identity:

```sh
twig auth login --identity work
twig auth login --identity corporate --tenant <tenant-id>
```

Normal sign-in uses interactive loopback PKCE. Device code is available only
where tenant policy permits it. Azure CLI is not required and is not a credential
fallback for normal work.

Tenant domains are authority selectors, not token tenant IDs. When sign-in
returns no ID-token tenant claim, Twig resolves a domain from the Azure DevOps
access token issued by that authenticated exchange before registering the
identity. Registration still checks the refreshed token against that tenant and
principal and rejects an explicitly supplied mismatching tenant ID. This
normalization is shared by PKCE and device-code sign-in; it does not add a
credential fallback or an automatic retry.

Adding another connection does not require enrolling the same account again.
The native Canvas setup can list saved AAD identities and explicitly select an
exact identity for discovery, reusing its stored credential without launching
sign-in. Discovery still verifies the registered principal and provider access;
a missing, changed or revoked credential never selects another account.

Bound AAD access tokens are cached for up to 50 minutes, with a five-minute
expiry safety margin. Refresh is demand-driven: a request that cannot use a
cached token exchanges that identity's stored refresh token silently. There is
no periodic interactive sign-in timer. AAD rejection, revocation or tenant policy
can require explicit sign-in again; this renews the same alias, not a new account.

Alternatively, enroll a PAT with a non-echoing prompt:

```sh
twig auth pat --identity work-pat --org contoso
```

`--stdin` reads one secret line for explicitly managed automation. Never put PATs
in arguments, notes, reports, or logs. Enrollment attests the actual principal;
a display name or alias is not identity proof.

Inspect identities, register the project, bind the chosen identity, and
initialize a fresh worktree:

```sh
twig auth identities -o json
twig connection add --provider ado --org contoso --project MyProject
twig connection bind --provider ado --org contoso --project MyProject --identity work
twig connection list --provider ado --org contoso --project MyProject -o json
twig init contoso MyProject
```

Binding a second identity adds a candidate; it never changes an existing pin.
When multiple identities match, `init --binding <binding-id>`
selects an existing matching binding for a **fresh** checkout without changing
the registered connection's selection. The last login is not a selection.

## GitHub

Enroll the account using `gh` separately, then initialize with its exact name:

```sh
twig init --provider github --host github.com --repository owner/repo --account my-account
```

Credential acquisition names that account explicitly and strips ambient token
variables. The active `gh` account is not an implicit override. Native immutable
repository and actor identity, rather than a repository URL alone, bind the store.
Explicit GitHub App enrollment/status is a separate credential route; it does
not prove the pinned `gh` actor's access.

## Inspect and renew

```sh
twig auth status -o json
twig connection status -o json
twig show 12345 --refresh -o json
```

The first two inspect local selection. The last performs actual work HTTP.
Renew the selected AAD identity with `auth login`, or the selected PAT with
`auth pat`; use `--identity <alias>` to name the enrollment deliberately.
Same-principal renewal does not silently select a sibling identity.

Missing or mismatched bindings must be corrected explicitly. Identity/target
hot-switch, `auth clear`, `auth logout`, connection migration/pin/default-transition,
and reset/reinitialize routes are retired. There is no migration or ambient-token
escape hatch. Use a fresh workspace for a different connection.

## Storage and failure recovery

Native AAD enrollment reports fixed, credential-free reasons for refresh-token
exchange and access-token claim failures. These distinguish AAD rejection,
exchange unavailability, malformed tokens, wrong audience, missing claims and
principal mismatches without exposing token contents, identity aliases or claim
values. The native phase and possible/confirmed local effects still describe
partial setup; a diagnostic is not permission to retry or replay an operation.

An explicit absolute `TWIG_USER_HOME` isolates native metadata. Credentials are
stored separately from portable repository configuration and normal output.
Private PAT files use owner-only permissions; file-backed storage is not an OS
credential vault.

If selection or credential admission fails, inspect the named identities and
bindings, then renew the intended principal. Do not try another identity merely
to get a blocked write through. `connection writes` provides read-only native
transport evidence. Only the original Core plan journal is execution
authority; a matching later value or a fresh local workspace does not justify
replaying an uncertain request.

The server adds same-owner Windows authentication and scoped views; it does not
replace provider identity or publication checks. See [server security](../server.md#security-and-data-ownership).
