---
command: init
group: getting-started
summary: Initialize a new Twig workspace at the current git-worktree root.
stability: stable
mutates: local
---

# `twig init`

`twig init` bootstraps a Twig workspace under the current git-worktree root.
It detects the worktree anchor, creates `.twig/`, writes the committed
`twig.json`, initializes the per-workspace SQLite cache, and captures the
process description needed by downstream ADO commands. GitHub initialization is
local-only and does not fetch process metadata. Gitignored `.twig/config`
is written only for explicit local preferences (such as a detected user
identity); implicit auth, display and tracking defaults do not create it.
Use `init` when first pointing Twig at an ADO org/project or rebuilding a
workspace with `--reinitialize`.

Fresh init does not select a reference profile by default. With no existing
`profile` declaration and no `--profile`, it creates the normal workspace,
cache, and registry entry without adding a profile pin or selected-profile
policy records. This is the supported non-reference initialization path for
process metadata and ordinary work-item authoring. The absent-profile
exemption for the reference sprint-entry gate remains in effect, but
primary-scope attachment and claims are unavailable without a compatible
selected profile.

ADO onboarding is four distinct operations, in this order —
`init` performs only the third:

1. **Enroll an identity**, once per machine: [`twig auth login --identity <alias>`](../system/auth-login.md) (AAD) or `twig auth pat --identity <alias> --org <org>` (PAT).
2. **Bind the endpoint to that identity**: [`twig connection bind --org <org> --project <project> --identity <alias> --default`](../system/auth-login.md).
3. **Initialize the checkout**: `twig init <org> <project>` (this command), or `twig init <org> <project> --binding <id>` to select a registered binding for this fresh checkout only. It requires an explicitly bound identity for the endpoint it is given — there is no account/last-login/Azure CLI fallback, so skipping steps 1–2 fails with a named remediation instead of silently picking a credential.
4. **Verify metadata access**: [`twig process --refresh`](../process/process.md) makes read-only ADO metadata requests and refreshes the local process/field metadata caches. This verifies metadata access for the selected identity, not every work-item permission. [`twig connection status`](../system/auth-status.md) only inspects the local binding and identity; it never contacts ADO and cannot verify remote access.

If `twig connection status` reports `layout-marker-missing`, the local `.twig/`
layout is missing its marker or is incompatible with this Twig version. This is
not evidence of a missing identity or binding. The error names the affected
directory; inspect and back up its contents and check the Twig version first.
To deliberately archive and rebuild an ADO workspace, run
`twig init <org> <project> --reinitialize` from its worktree root. Status never
reinitializes, repairs, deletes, reauthenticates, or rebinds automatically.

## Synopsis

```
twig init [<org>] [<project>] [flags]
twig init --provider github --host <host> --repository <owner/name>
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<org>` | conditional | Azure DevOps organization, positionally. Required unless `--org` is supplied. The shipped examples use the positional spelling `twig init <org> <project>`. |
| `<project>` | conditional | Azure DevOps project, positionally as the second argument. Required unless `--project` is supplied. |

## Flags

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--org <string>` | string | none | Azure DevOps organization name (e.g. `contoso`). Named alternative to the positional argument. |
| `--project <string>` | string | none | Azure DevOps project name. Named alternative to the positional argument. |
| `--provider <string>` | string | `ado` | Explicit provider, `ado` or `github`. Never inferred from a remote. |
| `--host <string>` | string | none | Explicit bare lowercase GitHub host, such as `github.com`; requires GitHub provider selection. |
| `--repository <string>` | string | none | Explicit GitHub `owner/name`; requires GitHub provider selection. |
| `--team <string>` | string | project default team | Team name within the project. |
| `--git-project <string>` | string | same as `--project` | ADO project that hosts the git repository, when different from the work-item project. |
| `--binding <id>` | string | endpoint's central default | Registered ADO binding to use during initialization and record as the checkout-local attachment pin. Requires a fresh checkout with no `.twig/`; cannot combine with `--force` or `--reinitialize`. Does not change the central default. |
| `--profile <identity>` | string | none | Opaque released profile identity to explicitly select for a fresh/untracked manifest. Does not discover live-process compatibility, choose from the detected process template, or override a tracked manifest. |
| `--force` | bool | `false` | Overwrite an existing workspace configuration in place, subject to tracked-manifest protection. Does not repair, replace, or migrate an existing profile declaration or selection. |
| `-o`, `--output <string>` | enum | `human` | Output format: `human`, `json`, `minimal`. |
| `--sprint <string>` | string | none | Sprint expression(s) to subscribe to (e.g. `@current`, `@current-1`). Semicolon-separated for multiple. Each expression is parsed by `IterationExpression.Parse` before any filesystem write. |
| `--area <string>` | string | none | Area path(s) to filter by (e.g. `Project\Team`). Append `:exact` for an exact match. Semicolon-separated for multiple. |
| `--reinitialize` | bool | `false` | Archive an existing `.twig/` tree to `.twig-legacy-<timestamp>/` and start clean. This is the design §7 supported legacy-recovery path — there is no in-place migration. Existing profile declarations are preserved and validated, not repaired or replaced. |

## Behavior

`init` refuses to write anything until every input-only check has passed (`src/Twig/Commands/InitCommand.cs`):

1. **Worktree anchor detection.** `WorktreeAnchorDetector.TryDetect` runs first; any failure (non-git checkout, bare repo, detached path) aborts before any filesystem mutation (`src/Twig/Commands/InitCommand.cs`).
2. **Root enforcement.** The invocation directory must be the git worktree root, verified via `git rev-parse --show-prefix` (empty at the root on every platform). Nested invocations are refused with `Managed init refused: invocation directory ... is not the git worktree root ...` (`src/Twig/Commands/InitCommand.cs`).
3. **`.twig/` scoping.** The workspace is always created at `<worktree-root>/.twig/`. A nested repo can never rewrite an ancestor's `.twig/` — this is the defect §3.1 fixes (`src/Twig/Commands/InitCommand.cs`).
4. **Repo-manifest preservation.** If a tracked `twig.json` already lives at the target, the split configuration is loaded and merged; conflicting org/project/team coordinates or `--git-project`/`--sprint`/`--area` overrides are rejected before mutation (`src/Twig/Commands/InitCommand.cs`).
5. **Sprint/area validation.** `--sprint` and `--area` are parsed and validated eagerly so a bad flag returns 1 without touching disk (`src/Twig/Commands/InitCommand.cs`).
6. **Profile declaration and selection.** A present top-level `profile` block is validated before new workspace state is written and is never repaired or replaced, including with `--profile`, `--force`, or `--reinitialize`. Incomplete or mismatched pins fail closed with the existing named profile errors. An explicit selection must match the shipped artifact and must not switch an existing selection. Unknown identities fail with `profile-identity-unknown`; there is no embedded-default fallback. Existing policy records alone do not declare a profile.
7. **Managed init transaction.** Filesystem writes are recorded in `InitRollbackJournal`; a failure at any later step restores overwritten files byte-for-byte and deletes anything created this run (`src/Twig/Commands/InitCommand.cs`).
8. **Telemetry.** Emits a `CommandExecuted` event with `command=init`, `exit_code`, `output_format`, `had_global_profile`, and generic `duration_ms` / `field_count` metrics — no org, project, or template names ever leave the process (`src/Twig/Commands/InitCommand.cs`).

An explicit `--binding` is checked against the declared endpoint's registered
bindings before any checkout filesystem mutation or metadata HTTP request.
Unknown, empty or cross-endpoint IDs fail without creating `.twig/` or changing
`twig.json`. Authentication uses the selected binding even if the central default
cannot authenticate, or no central default exists. On success the pin is stored
in `.twig/attachment.json`, the same native attachment field used by
`twig connection pin`; `twig connection status` reports `checkout-binding-pin`.
The central default and other checkouts remain untouched. This is initial
selection, not an identity transition: existing local state, `--force`, and
`--reinitialize` are refused with `--binding`. Existing checkouts must use the
guarded `twig connection pin` workflow instead.

On success `.twig/cache/twig.db` exists, `twig.json` records the coordinates, and `.twig/` is appended to `.gitignore` (SEC-001).

### GitHub checkout onboarding

Start at a fresh git-worktree root:

```sh
twig init --provider github --host github.com --repository owner/repo
twig config github.account your-existing-login
twig provider discover github --host github.com --repository owner/repo -o json
```

GitHub init creates the local layout, registry entry and neutral item cache. It
records the explicitly supplied host/repository in `github` and uses those same
values as the legacy storage coordinate fields `organization`/`project`; these
are not an ADO endpoint or a remote identity binding. It does not inspect remotes,
acquire credentials, make HTTP requests, select a profile, bind a repository,
enroll an account, or write a remote issue. JSON output is one structured record.
Missing or malformed scope refuses before writing; nothing defaults to github.com.

The credential choice is separate: the example selects an already enrolled `gh`
account. Alternatively, explicitly enroll/select a
[GitHub App identity](../system/auth-login.md#github-app-oauth-enrollment).
Discovery is a read-only remote lookup using that explicit credential. Review its
opaque owner/repository IDs and attachment revision, then bind those exact values:

```sh
twig provider bind github --host github.com --repository owner/repo --expected-owner-id O_id --expected-repository-id R_id --expected-attachment-revision 0
twig sync --pull-only -o json
```

`O_id`, `R_id` and `0` are illustrative, not values to copy over an existing
attachment. Use fresh discovery's actual IDs/revision; replacements also require
the current binding ID. Binding changes local target selection, not permissions.
Neither discover nor bind changes the manifest.

GitHub reads use canonical local identities (`twig show --local-id <guid> -o json`).
Mutations use reviewed, digest-bound proposal files, not ADO integer-ID commands
or an implicit sync push. Unsupported legacy ADO commands refuse; selection never
falls back to ADO. See [sync](sync.md) and [proposal apply](../plans/proposal-apply.md).
This onboarding is not live-provider or release-candidate qualification.

ADO positional/named coordinates cannot be mixed into the GitHub CLI spelling.
GitHub init also refuses ADO team/sprint/area/git-project/profile/binding options and
`--force`/`--reinitialize`; it does not migrate or overwrite existing work.
Tracked manifest provider/scope conflicts are rejected, not silently retargeted.
Existing manifest configuration can be edited deliberately with `twig config`,
but config edits alone cannot initialize storage, migrate ADO data or establish
repository identity.

### What profile selection means

`--profile <identity>` explicitly declares a shipped reference release; it is
not evidence that an arbitrary live process is compatible. The currently
shipped artifact declares `twig.reference-profile.hyperbright`, profile
version `1.0.0`, and base process tailoring version `basic:2026-08-24:1`.
Selecting it does not make a stock Agile or Scrum process match this
Basic-derived reference, and init adds no new live-compatibility discovery.

The top-level pin fields `profile.identity`, `profile.profileVersion`, and
`profile.baseProcessVersion` must byte-equal the artifact's `identity`,
`profileVersion`, and `baseProcess.tailoringVersion`, respectively.
`policy.selectedProfile` and `policy.primaryScopeTypes` record materialized
selection data; they are neither runtime eligibility overrides nor consent to
select a profile. Profile-gated eligibility still comes from the provider.

`--profile` can add a declaration only to a fresh or untracked manifest. If
`twig.json` is already tracked and unprofiled, select the release through a
separate, explicit reviewed manifest change before initializing. A matching
existing pin is preserved without rewriting it or re-materializing policy
records.

Existing pin values remain unchanged, including broken values. A change to an
existing profile is a separate, explicit repository-owner migration decision;
init flags are not a migration or repair mechanism. Do not delete a profile
declaration or registry state to bypass refusal. See
[reference profile](../../features/reference-profile.md) for the exact-match
contract and named errors.

## Examples

Initialize a fresh non-reference workspace without declaring a profile:

```
$ twig init contoso Fabrikam
Initialized Twig workspace in /checkout/.twig
```

This leaves the reference sprint-entry gate exempt while preserving other
authoring checks. It does not enable primary-scope attachment or claims.

Select a previously registered binding without changing the endpoint default:

```sh
twig connection list --org contoso --project Fabrikam
twig init contoso Fabrikam --binding <id>
twig connection status
```

Use the exact binding ID returned by `connection list`. This does not enroll an
identity, register a binding, or switch another checkout's identity.

Explicitly declare the shipped reference release in a fresh/untracked manifest
for a repository intended to run that reference process:

```sh
twig init contoso Hyperbright --profile twig.reference-profile.hyperbright
```

This is selection, not automatic live-process compatibility validation.

Initialize a workspace, subscribing to the current and previous sprint and filtering to a single area path with exact matching:

```
$ twig init --org contoso --project Fabrikam \
    --sprint "@current;@current-1" \
    --area "Fabrikam\Team A:exact"
Initialized Twig workspace for contoso/Fabrikam.
Subscribed sprints: @current, @current-1
Area filters: Fabrikam\Team A (exact)
```

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Success | `0` |
| Missing org or project (positional or named) | `1` — prints `error: Usage: twig init <org> <project>, or twig init --org <org> --project <project>` (`src/Twig/Program.cs`) |
| Not inside a git worktree | `1` — `Managed init refused: not-a-git-worktree` |
| Invocation directory is not the worktree root | `1` — `Managed init refused: invocation directory ... is not the git worktree root ...` |
| Existing tracked `twig.json` conflicts with supplied coordinates | `1` — coordinate-conflict error message |
| Existing tracked `twig.json` conflicts with `--git-project`, `--sprint`, or `--area` overrides | `1` — override-conflict error message |
| `--profile` would add a declaration to an already tracked, unprofiled `twig.json` | `1` — `Init cannot add a profile declaration to tracked twig.json. Select the profile deliberately in the manifest before initializing.` |
| Invalid `--sprint` expression | `1` — `Invalid sprint expression '<expr>': <parse error>` |
| Invalid `--area` path | `1` — `Invalid area path '<path>': <parse error>` |
| Explicit binding ID is empty, unknown, or belongs to another endpoint | `1` — `binding-selector-required`, `binding-unknown`, or `binding-endpoint-mismatch`; no checkout files are written |
| `--binding` with existing `.twig/`, `--force`, or `--reinitialize` | `1` — `binding-initialization-requires-fresh-checkout`; existing local state is preserved |
| Explicit profile identity is not shipped, or conflicts with an existing selected identity | `1` — `profile-identity-unknown`; no fallback selection or profile replacement |
| Present `profile` pin is incomplete | `1` — `profile-schema-invalid`; the declaration is preserved, not repaired |
| Present `profile` pin does not match the artifact | `1` — `profile-identity-unknown`, `profile-version-mismatch`, or `base-process-version-mismatch`; pin values remain unchanged |
| Explicit profile selection conflicts with an existing selected-profile policy version | `1` — `profile-version-mismatch`; selection records are not silently migrated |
| Managed-init transaction failure | `1` — `InitRollbackJournal` restores overwritten files and removes anything created this run |

## See also

- [`twig sync`](./sync.md)
- [`twig refresh`](./refresh.md)
- [Reference profile](../../features/reference-profile.md)
