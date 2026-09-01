---
command: proposal validate
group: plans
summary: Validate a proposal v1 file without touching ADO.
stability: stable
mutates: none
---

# `twig proposal validate`

Read-only check that a proposal v1 JSON file parses, canonicalizes, and
reports a stable SHA-256 digest. `validate` never talks to Azure DevOps and
never touches the journal — it is the first step on the change-proposal
path and the one you can run freely while authoring a proposal.

## Synopsis

```
twig proposal validate --file <path> [-o human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--file`|string|_none_|Path to the proposal v1 JSON file. Must resolve inside the current workspace root.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `IPlanLifecycleService.ValidateAsync`
(`src/Twig/Commands/PlanCommand.cs:48-59`). The service parses the file,
canonicalizes it, produces a lowercase-hex SHA-256 digest over the canonical
bytes, and reports any structural or semantic issues (`PlanValidationResult`).
Behaviors worth naming:

- **No ADO mutation, no journal write.** Validate is safe to run repeatedly
  and never contacts the server.
- **Workspace guard.** The file path must resolve inside the active workspace
  root; a path outside the workspace is a lifecycle input error, not a usage
  error (`src/Twig/Commands/PlanCommand.cs:193-207`).
- **Digest is stable.** The digest returned here is byte-identical to the one
  `proposal preview` reports and the one `proposal apply --confirm` requires.

## Examples

Validate a proposal for interactive review:

```console
$ twig proposal validate --file .twig/proposals/close-1234.json
proposal: valid  digest=3f9c…a1b7
```

Emit machine-readable output for a script that will later confirm the
digest:

```console
$ twig proposal validate --file .twig/proposals/close-1234.json -o json
{ "valid": true, "digest": "3f9c…a1b7", "issues": [] }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Proposal parses and passes every validator.|`0`|
|Proposal parses but validator raised at least one issue.|`1`|
|`--file` omitted, or file path could not be resolved.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — next step after a clean validate.
- [`proposal apply`](proposal-apply.md) — consumes the digest this command reports.
- [`plan validate`](plan-validate.md) — deprecated alias.
