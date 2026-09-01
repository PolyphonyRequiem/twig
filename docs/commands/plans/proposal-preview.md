---
command: proposal preview
group: plans
summary: Preview a proposal — journal import, pending snapshot, digest, and canApply gate.
stability: stable
mutates: local
---

# `twig proposal preview`

Reads a proposal v1 file, imports its journal row (if any), snapshots every
currently-staged pending change, and reports the canonical digest and the
`canApply` gate — the boolean that `proposal apply` will consult. Preview
never mutates ADO; the "local" mutation flag reflects that importing a
journal row is a write into the workspace's per-proposal store.

## Synopsis

```
twig proposal preview --file <path> [-o human|json|minimal]
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

Delegates to `IPlanLifecycleService.PreviewAsync`
(`src/Twig/Commands/PlanCommand.cs:66-77`). The preview surface is
deliberately verbose so a caller can confirm intent before authorizing an
apply:

- **Canonical digest.** Recomputed exactly as validate reports it; this is
  the value the caller will pass to `proposal apply --confirm`.
- **Journal import.** The journal row keyed on the digest is loaded into the
  workspace store; a fresh proposal creates a row in its initial state, and
  a previously-imported proposal has its row refreshed.
- **Pending snapshot.** Every staged pending change is captured in exact
  staging order (`PlanPreviewResult.PendingChanges`). Any pending row makes
  `canApply` false — proposal v1 is declarative-only and will not
  auto-flush pending edits
  (`src/Twig.Domain/Services/Plan/PlanPreviewResult.cs:26-37`).
- **`canApply` gate.** True iff the proposal is valid, its workspace matches
  the active config, no pending row exists, and the journal was imported
  successfully. False otherwise, and the reason is on `Issues`.

## Examples

Human preview before authorization:

```console
$ twig proposal preview --file .twig/proposals/close-1234.json
proposal: digest=3f9c…a1b7  canApply=true
pending changes: 0
operations: 3 planned
```

Machine preview for an agent that will script the apply:

```console
$ twig proposal preview --file .twig/proposals/close-1234.json -o json
{
  "digest": "3f9c…a1b7",
  "canApply": true,
  "pendingChanges": [],
  "operations": [ /* declared ops */ ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Preview succeeded (even when `canApply=false` because of pending rows).|`0`|
|Proposal invalid — validation issues raised.|`1`|
|`--file` omitted, or file path could not be resolved.|`2`|

## See also

- [`proposal validate`](proposal-validate.md) — cheaper check when you only need the digest.
- [`proposal apply`](proposal-apply.md) — consumes the digest reported here.
- [`pending`](pending.md) — same rows that block `canApply`.
- [`plan preview`](plan-preview.md) — deprecated alias.
