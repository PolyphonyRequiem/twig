---
command: proposal status
group: plans
summary: Show journal state for a proposal file, keyed on its digest.
stability: stable
mutates: none
---

# `twig proposal status`

Reads the workspace's per-proposal journal and reports the current state
for the file's digest — the top-level plan state, per-operation states in
declaration order, any terminal error captured on apply. Cache-only; makes
no ADO calls.

## Synopsis

```
twig proposal status --file <path> [-o human|json|minimal]
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

Delegates to `IPlanLifecycleService.StatusAsync`
(`src/Twig/Commands/PlanCommand.cs:145-166`). The result carries three
distinct shapes and the exit code discriminates between them
(`src/Twig.Domain/Services/Plan/PlanStatusResult.cs:6-22`):

1. **Journal loaded.** `Found=true` with `Digest`, `State`, `Operations`,
   and (on prior apply failure) `Error` populated. Exit 0.
2. **Valid digest, no journal.** The lifecycle service returns `null` and
   the command emits a `proposalStatusNotFound` document
   (`src/Twig/Commands/PlanCommand.cs:154-158`). Exit 1 — the file parsed
   cleanly but has never been previewed.
3. **Input error.** The lifecycle service returns a non-null result with
   `Found=false` and `Issues` populated (path outside workspace, unreadable
   file, invalid JSON, workspace mismatch). Exit 2.

Status never mutates the journal. If the digest has moved because the file
was re-edited, re-run `proposal preview` to import the current row before
consulting status.

## Examples

Human status for a previewed but not-yet-applied proposal:

```console
$ twig proposal status --file .twig/proposals/close-1234.json
digest: 3f9c…a1b7
state:  Previewed
ops:    3 (all Pending)
```

Machine snapshot for an agent:

```console
$ twig proposal status --file .twig/proposals/close-1234.json -o json
{
  "digest": "3f9c…a1b7",
  "found": true,
  "state": "Applied",
  "operations": [ /* ordinal + state + error */ ],
  "error": null
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Journal row loaded for the file's digest.|`0`|
|File parsed and yielded a digest, but no journal has ever been imported for it.|`1`|
|Input error — path outside workspace, unreadable file, invalid JSON, workspace mismatch.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — imports the journal row this command reads.
- [`proposal apply`](proposal-apply.md) — writes the row this command reports.
- [`plan status`](plan-status.md) — deprecated alias.
