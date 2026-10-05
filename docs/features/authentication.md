# Authentication

Twig's supported CLI authenticates through an **explicit connection binding**,
not whichever account last signed in on the machine. A binding joins an Azure
DevOps endpoint to a registered Azure Active Directory (AAD) or Personal Access Token (PAT) identity. A worktree attachment selects
a checkout-local pin or that endpoint's central default; normal ADO clients use
only the credential reference and principal admitted by that selection.

MCP is withdrawn from supported builds, installation and companion downloads
pending redesign. TUI and Herdr connection integrations remain deferred; their
completion is not part of the CLI cutover. Retained draft sources are not proof
that those integrations are delivered. Existing legacy host processes can still
be migration blockers and must be explicitly closed when reported.

## Enroll and select an identity

AAD enrollment uses interactive loopback PKCE, with device-code available when
permitted by tenant policy. Azure CLI is **not required** for normal CLI
authentication. Name the identity explicitly when enrolling before attachment:

```text
twig auth login --identity personal
twig auth login --identity corporate --tenant <tenant-id>
```

PAT enrollment reads a non-echoing prompt or one line of standard input with
`--stdin`; never put the secret in a command argument:

```text
twig auth pat --identity corporate-pat --org <organization>
```

For AAD, authenticated token issuance establishes tenant/object/issuer evidence.
JWT decoding is consistency checking, not local signature verification. For PAT,
a read-only `/_apis/connectionData` probe establishes the authoritative ADO
principal at the HTTPS organization authority. Aliases and display names are
labels, not authority evidence. Re-enrolling an existing alias requires the same
principal, method and authority; an account mismatch leaves its credential intact.

Inspect identities, then create the initial endpoint binding:

```text
twig auth identities -o json
twig connection bind --org <organization> --project <project> --identity personal --default
twig connection list --org <organization> --project <project> -o json
```

The identity must already be registered. Explicit organization/project arguments
must be paired. Creating a binding without `--default` does not implicitly select
it. A different existing default refuses; use the guarded default transition
rather than treating `connection bind` as an identity switch. Binding management
alone does not attach a worktree or authorize work requests from an unattached
checkout. See [`auth login` / `auth pat`](../commands/system/auth-login.md).

## Normal work and renewal

```text
twig auth status -o json
twig connection status -o json
twig show <id> --refresh -o json
twig sync --pull-only
```

Status uses the same attachment/binding resolver as normal work and reports
selection revisions, method-specific principal metadata and configuration
provenance without printing credentials. It is a local diagnostic, **not proof of
fresh remote access**; an actual refreshed read or pull-only sync exercises that
access. Pull-only sync never flushes queued local edits.

An attached binding cannot be overridden by `TWIG_PAT`, raw repository auth
configuration, an Azure CLI active account, a machine token cache, or the global
last login. Missing, cross-endpoint or stale selection fails closed. AAD refresh
stays bound to the enrolled principal; it does not fall through to a sibling
account. PAT admission is tied to the exact saved material, authority and principal,
and replacement material must be freshly attested before work HTTP.

To renew the selected identity, run `twig auth login` for AAD or `twig auth pat`
for PAT. `--identity <alias>` explicitly renews a named identity without selecting
it for another connection. Verified same-principal renewal preserves selection
revisions, local work and read data. `twig auth clear` invalidates only the selected
identity's access/admission proof; it does not delete its PAT or another identity's
credentials. See [`auth clear`](../commands/system/auth-clear.md).

## Change a selection safely

Inspect prerequisites before changing a checkout's identity:

```text
twig connection check -o json
twig connection pin --binding <binding-id> -o json
twig connection pin --binding <binding-id> --confirm <preview-digest> -o json
twig connection unpin -o json
```

`pin` and `unpin` are preview-first guarded transitions. `unpin` returns to the
explicit endpoint default; apply it with its own exact preview digest. Default
changes similarly preflight affected registered, unpinned checkouts. Pending
edits, seeds, unsettled writes/journals, claims, incompatible holders or unknown
evidence can block a transition; inspection never publishes/discards work,
releases a claim or selects an alternate account.

A changed selection requires explicit reconnect of an old runtime; it cannot
silently adopt another actor. The affected disposable read mirror starts cold,
while credentials, portable policy and durable local work/proposal history are
preserved. An interrupted transition fences work until its original arguments and
native intent are resumed; do not delete the intent or invent a replacement.
See [`auth status` and connection administration](../commands/system/auth-status.md).

## Migrate legacy storage deliberately

Legacy `auth.method`, raw PATs and global refresh/token files are migration inputs,
not supported normal-work selectors. Inspect without activating:

```text
twig connection migrate --identity <alias> -o json
twig connection migrate --identity <alias> --method aad -o json
twig connection migrate --identity <alias> --method pat -o json
```

Use an existing registered identity or explicitly import supported legacy evidence
with the selected method. Missing principal evidence requires separate enrollment;
a global login, matching audience or display name never establishes intended
connection ownership. Apply only a clean preview with the same identity/method
arguments and `--confirm <digest>`.

Split mirror versions 16 and 17 are supported migration inputs. Preserve earlier
unsplit or unknown layouts and recover them with a compatible Twig version.
Activation checks actual legacy host/store closure, preserves staged work and
durable history, and seals legacy mirror entry points. It does not kill hosts,
flush work, replace another default or offer a force bypass. Interrupted activation
resumes its original mapping and native intent; do not use `init --force` or
`--reinitialize` to erase admitted storage. See the detailed
[migration contract](../commands/system/auth-status.md#legacy-migration-administration).

## Failure recovery and security

- **Missing selection:** inspect `auth identities` and `connection list`; explicitly
  establish the intended binding and attachment. Do not try ambient login fallback.
- **Expired or refused credential:** inspect `auth status`, then renew the selected
  AAD identity with `auth login` or PAT with `auth pat`. A principal mismatch must
  be corrected deliberately, not by retrying as another account.
- **Empty process cache:** after fixing the binding, run `twig sync --pull-only`.
- **Unknown write outcome:** inspect `connection writes` and the original proposal
  journal. Use native reconciliation under the original identity and exact digest;
  a later matching value is not evidence permitting a replay.
- **Policy-blocked device code:** use loopback `auth login` where permitted. For a
  headless session, use device code only if policy allows it; no policy bypass.

`TWIG_USER_HOME` can isolate the metadata home when explicitly set to an absolute
path. Private credential files are separate from central registry metadata and
portable configuration; tokens and PATs are never included in command output or
telemetry. Do not paste private files into logs or reports. PAT files are created
user-private before secret bytes are written (owner-only Windows ACLs or Unix
`0600`). This is private file-backed storage, not an OS credential vault.

## Glossary

- **Identity:** enrolled principal metadata plus a private credential reference.
- **Binding:** an explicit endpoint-to-identity association.
- **Attachment:** the registration tying a Git checkout to its declared connection.
- **Pin:** a checkout-local binding selection overriding the endpoint default.
- **Mirror:** disposable cached read data, separate from durable local work.
- **Admission:** verification permitting an operation under one frozen selection and principal.

## See also


- [`auth login` / `auth pat`](../commands/system/auth-login.md)
- [`auth status` and connection administration](../commands/system/auth-status.md)
- [`auth clear`](../commands/system/auth-clear.md)
- [ADO integration architecture](../architecture/ado-integration.md)
