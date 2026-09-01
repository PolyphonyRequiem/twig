---
command: plan status
group: plans
summary: Deprecated alias for `proposal status`.
stability: stable
mutates: none
---

# `twig plan status`

Deprecated alias for [`twig proposal status`](proposal-status.md). The two
verbs are registered as `[Command("proposal status|plan status")]` in
`src/Twig/Program.cs:1383-1385` and dispatch to the same
`PlanCommand.StatusAsync` handler, so behavior, flags, exit codes, and
output are identical.

Prefer the canonical `proposal status` form. The legacy name remains valid
indefinitely, but grouped help and documentation lead with `proposal`.

## Synopsis

```
twig plan status --file <path> [-o human|json|minimal]
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

Alias only. See [`proposal status`](proposal-status.md#behavior) for the
three-outcome result — journal loaded, valid digest without journal, or
input error — and the exit-code discriminator.

## Examples

Equivalent invocations:

```console
$ twig plan status --file .twig/proposals/close-1234.json
$ twig proposal status --file .twig/proposals/close-1234.json
```

Machine output:

```console
$ twig plan status --file .twig/proposals/close-1234.json -o json
{ "digest": "3f9c…a1b7", "found": true, "state": "Applied" }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Journal row loaded for the file's digest.|`0`|
|File parsed and yielded a digest, but no journal has ever been imported for it.|`1`|
|Input error — path outside workspace, unreadable file, invalid JSON, workspace mismatch.|`2`|

## See also

- [`proposal status`](proposal-status.md) — **canonical form; prefer this.**
- [Plans group overview](README.md) — proposal/plan naming cutover.
