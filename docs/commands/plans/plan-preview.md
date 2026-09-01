---
command: plan preview
group: plans
summary: Deprecated alias for `proposal preview`.
stability: stable
mutates: local
---

# `twig plan preview`

Deprecated alias for [`twig proposal preview`](proposal-preview.md). The
two verbs are registered as `[Command("proposal preview|plan preview")]`
in `src/Twig/Program.cs:1366-1368` and dispatch to the same
`PlanCommand.PreviewAsync` handler, so behavior, flags, exit codes, and
output are identical.

Prefer the canonical `proposal preview` form. The legacy name remains
valid indefinitely, but grouped help and documentation lead with
`proposal`.

## Synopsis

```
twig plan preview --file <path> [-o human|json|minimal]
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

Alias only. See [`proposal preview`](proposal-preview.md#behavior) for the
full behavior contract — canonical digest, journal import, pending
snapshot, and the `canApply` gate.

## Examples

Equivalent invocations:

```console
$ twig plan preview --file .twig/proposals/close-1234.json
$ twig proposal preview --file .twig/proposals/close-1234.json
```

Machine output:

```console
$ twig plan preview --file .twig/proposals/close-1234.json -o json
{ "digest": "3f9c…a1b7", "canApply": true, "pendingChanges": [] }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Preview succeeded (even when `canApply=false` because of pending rows).|`0`|
|Proposal invalid — validation issues raised.|`1`|
|`--file` omitted, or file path could not be resolved.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — **canonical form; prefer this.**
- [Plans group overview](README.md) — proposal/plan naming cutover.
