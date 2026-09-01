---
command: plan validate
group: plans
summary: Deprecated alias for `proposal validate`.
stability: stable
mutates: none
---

# `twig plan validate`

Deprecated alias for [`twig proposal validate`](proposal-validate.md). The
two verbs are registered as `[Command("proposal validate|plan validate")]`
in `src/Twig/Program.cs:1359-1361` and dispatch to the same
`PlanCommand.ValidateAsync` handler, so behavior, flags, exit codes, and
output are identical.

Prefer the canonical `proposal validate` form for new scripts. The legacy
name remains valid indefinitely — no scripted uses will break — but grouped
help and documentation lead with `proposal`.

## Synopsis

```
twig plan validate --file <path> [-o human|json|minimal]
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

Alias only. See [`proposal validate`](proposal-validate.md#behavior) for the
full behavior contract, including the workspace guard, the canonical digest
computation, and the fact that no ADO calls are made.

## Examples

Equivalent invocations:

```console
$ twig plan validate --file .twig/proposals/close-1234.json
$ twig proposal validate --file .twig/proposals/close-1234.json
```

Machine output:

```console
$ twig plan validate --file .twig/proposals/close-1234.json -o json
{ "valid": true, "digest": "3f9c…a1b7", "issues": [] }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Proposal parses and passes every validator.|`0`|
|Proposal parses but validator raised at least one issue.|`1`|
|`--file` omitted, or file path could not be resolved.|`2`|

## See also

- [`proposal validate`](proposal-validate.md) — **canonical form; prefer this.**
- [Plans group overview](README.md) — proposal/plan naming cutover.
