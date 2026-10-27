---
command: plan apply
group: plans
summary: Deprecated alias for `proposal apply`.
stability: stable
mutates: ado
---

# `twig plan apply`

Deprecated alias for [`twig proposal apply`](proposal-apply.md). The two
verbs are registered as `[Command("proposal apply|plan apply")]` in
`src/Twig/Program.cs:1376-1378` and dispatch to the same
`PlanCommand.ApplyAsync` handler, so behavior, flags, exit codes, and
output are identical.

Prefer the canonical `proposal apply` form. The legacy name remains valid
indefinitely, but grouped help and documentation lead with `proposal`.

## Synopsis

```
twig plan apply --file <path> --confirm <digest>
                [-o human|json|minimal]
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
|`--file`|string|_none_|Path to the proposal JSON file. Must resolve inside the current workspace root.|
|`--confirm`|string|_none_|Lowercase-hex SHA-256 digest of the canonical proposal bytes. Must match exactly.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Alias only. See [`proposal apply`](proposal-apply.md#behavior) for the full
behavior contract — digest gate, per-operation
journal writes, and the all-or-nothing exit rule.

## Examples

Equivalent invocations:

```console
$ twig plan apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7
$ twig proposal apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7
```

Machine output for an agent script:

```console
$ twig plan apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7 -o json
{ "digest": "3f9c…a1b7", "failed": false, "operations": [ /* rows */ ] }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Every operation reached the Verified terminal state.|`0`|
|One or more operations failed, or digest did not match.|`1`|
|`--file` or `--confirm` omitted.|`2`|

## See also

- [`proposal apply`](proposal-apply.md) — **canonical form; prefer this.**
- [Plans group overview](README.md) — proposal/plan naming cutover.
