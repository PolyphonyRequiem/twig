---
command: auth status
group: system
summary: Inspect the effective attached identity binding and configuration provenance without secrets.
stability: stable
mutates: none
---

# `twig auth status`

Reports the effective binding through the same resolver as `twig connection
status`. It does not inspect an ambient global token as if that token were the
selected account. This is integration-branch behavior, not a partial release.

## Synopsis

```text
twig auth status [-o <format>]
twig connection status [-o <format>]
twig auth identities [-o <format>]
twig connection list [--org <organization> --project <project>] [-o <format>]
twig connection check [-o <format>]
twig connection migrate --identity <alias> [--method aad|pat] [--confirm <digest>] [-o <format>]
twig connection pin --binding <binding-id> [--confirm <digest>] [-o <format>]
twig connection unpin [--confirm <digest>] [-o <format>]
twig connection writes [-o <format>]
twig connection reconcile-write --intent <id> --confirm <digest> --authorize <identity-id> --rationale <basis> [-o <format>]
```

## Behavior

Status validates the worktree attachment, declared endpoint and effective local
pin or central binding default. It reports organization/project/team, method, alias, stable principal
metadata, binding and selection revisions, attachment revision, and configuration
source paths. `display.icons` reports its winning local/global/default source;
local preference source is not claimed as the origin of inherited global icons.
Human and structured output use the same admitted snapshot. Status performs no
work-item request and does not claim fresh remote authorization or locally
verified JWT signatures.

AAD metadata includes tenant/object/issuer requirements. PAT metadata includes
`adoPrincipalId` and `adoAuthority`; PAT output omits AAD-only claims. Names are
management aliases and display labels, never authoritative principal evidence.

Identity listing is explicit bootstrap administration and works before attachment.
Connection listing inspects bindings for paired explicit `--org`/`--project`
coordinates, or the current configuration's endpoint. Neither command selects
an account or changes a default. Registry listings contain principal metadata and
opaque references, never refresh/access tokens or PATs.

Missing, unknown or cross-endpoint selection refuses rather than using a machine
cache, Azure CLI account, global token store or last login. An initial binding can
be created with `twig connection bind --identity <alias> --default`; explicit
coordinates use paired `--org` and `--project`. A different existing default
refuses; safe identity switching is a separate guarded management transition.

### Binding-change prerequisites

`twig connection check` inspects the attached worktree's local prerequisites for
an identity change without changing its binding, publishing or discarding work,
releasing claims, or making work-item requests. The MCP equivalent is
`twig_connection_check`.

JSON output includes the effective selection and attachment revisions plus exact
pending edits, local seeds, open publish intents, unresolved journal operations,
reserved claims, worktree guards and unknown rows. Unknown or unresolved evidence
blocks eligibility; a later successful operation does not erase an earlier
unsettled journal row. Actionable next steps describe separate native operations,
not automatic remediation. There is no force bypass.

This is a prerequisite snapshot, not authorization to switch an identity or proof
of fresh remote access. An eligible snapshot returns `0`; a blocked snapshot
returns `1`, retaining its structured evidence.

### Guarded checkout pin and removal

`twig connection pin --binding <id>` previews a checkout-local pin; `twig
connection unpin` previews returning to the endpoint's explicit default. Inspect
binding IDs with `connection list`, then apply the same arguments with
`--confirm <digest>`. Missing, cross-endpoint or stale selections refuse. Actual
pin changes require the native migrated layout and fresh operation/attachment
fences; they never activate migration, publish/discard work, or rewrite holders.

An interrupted transition fences reads, writes and ordinary attachment changes.
Resume its original arguments and digest after resolving the reported blocker;
do not delete its native intent or start a replacement. Completion resets only
the one disposable mirror when the effective binding changes. Credentials,
portable policy, primary scope and durable pending/proposal history survive.
Old live runtimes require explicit reconnect; verified same-principal credential
renewal and an identical pin no-op do not erase data or adopt another actor.

### Native uncertain writes

Mutating requests record immutable native admission before HTTP. Lost responses,
process death and ambiguous server outcomes remain blockers even after physical
leases disappear. `connection writes` lists original identities, exact request
digests, response observations and append-only outcome receipts. Inspection never
replays a write; clearing a pending note does not settle its unknown remote POST.

`connection reconcile-write` requires the exact digest, original registered
identity ID and truthful rationale. It performs read-only original-actor evidence
collection: an exhausted work-item revision test and the exact immutable first
post-CAS revision can establish the requested effect. A later matching current
value, matching comment text/actor, an expired lease or rationale alone cannot.
An unrevisioned lost POST without attributable server evidence remains blocked;
there is no force, automatic retirement or alternate-account retry. Reconciliation
returns `0` only with a native outcome receipt; unresolved evidence returns `1`.

### Legacy migration administration

`twig connection migrate` previews a versioned import without activating storage.
Use `--identity` to explicitly map the endpoint and preserved local work to the
intended principal. For an unregistered identity, `--method aad` imports the
metadata home's legacy refresh credential; `--method pat` imports a legacy PAT
from the worktree's local configuration. Missing principal evidence requires
explicit enrollment. A global login, token audience, display name or last-used
account never establishes connection intent, and secrets are not accepted in
migration arguments or included in output.

Apply with the same identity/method arguments and `--confirm <digest>` from a
clean preview. Source changes, ambiguous mappings, incompatible claims,
in-flight native publication, unsupported versions and unreadable data refuse
before activation. Split mirror versions 16 and 17 are supported; earlier
unsplit/unknown layouts must be preserved and recovered with their compatible
Twig version. Migration never flushes pending work, publishes proposals,
rewrites journal states, replaces another default or weakens portable policy.

Activation inventories supported OS hosts and actual provider/store capabilities
and handles. Explicitly close affected CLI/TUI/MCP/Herdr subprocesses and legacy
providers/stores, then rerun. An empty lease ledger or an owner assertion is not
closure evidence. Unavailable/inconclusive inspection refuses; migration does
not kill processes, restart hosts automatically or offer a force bypass.

A native preparing intent fences interrupted activation. The disposable mirror
moves to a generation-admitted entry point; legacy SQLite paths are sealed, and
current opens require native admission rather than a supplied connection string.
Credentials, staged work, durable `pending.db`, historical proposals/receipts,
attachment and portable configuration remain preserved. Unprotected read data
starts cold. Repeating an active migration is idempotent; an interrupted run
resumes the original identity and native intent, never a replacement generation
or actor. Resolve its reported blocker and preview/apply again; do not erase the
intent, delete the durable store or reinitialize admitted storage with legacy
`init --force`/`--reinitialize`.

This is explicit management administration, not permission to issue normal work
requests outside a valid attachment. Preview output includes `state`, `digest`,
`canApply`, source versions, every blocker and separate next steps. Eligible
previews/active results return `0`; blocked/runtime failures return `1`; invalid
method arguments return `2`.

## Exit codes and failure modes

Successful inspection/listing returns `0`. Status returns `1` with setup guidance
when attachment or effective selection cannot be resolved. Passing only one of
`--org`/`--project` to connection listing refuses. Missing identity or an attempt
to replace a different initial default refuses without account fallback.

## See also

- [`auth clear`](auth-clear.md) — invalidate only the selected access cache.
- [`auth login`](auth-login.md) — explicitly enroll or renew an identity.
