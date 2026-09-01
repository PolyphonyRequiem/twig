---
command: plan seed
group: plans
summary: Deprecated alias for `proposal seed`.
stability: stable
mutates: none
---

# `twig plan seed`

Deprecated alias for [`twig proposal seed`](proposal-seed.md). The two
verbs are registered as `[Command("proposal seed|plan seed")]` in
`src/Twig/Program.cs:1390-1392` and dispatch to the same
`PlanCommand.DescribeSeedAsync` handler, so behavior, flags, exit codes,
and output are identical.

Prefer the canonical `proposal seed` form. The legacy name remains valid
indefinitely, but grouped help and documentation lead with `proposal`.

## Synopsis

```
twig plan seed --id <negative-alias> [-o human|json|minimal]
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
|`--id`|int|_none_|Negative display alias of a currently-staged seed.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Alias only. See [`proposal seed`](proposal-seed.md#behavior) for the
descriptor semantics, the negative-id requirement, and the not-found
outcome.

## Examples

Equivalent invocations:

```console
$ twig plan seed --id -42
$ twig proposal seed --id -42
```

Machine output:

```console
$ twig plan seed --id -42 -o json
{ "id": -42, "fingerprint": "8c1e…9dfa" }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Staged seed found; descriptor emitted.|`0`|
|`--id` refers to no currently-staged seed (positive id, unknown alias, or already published).|`1`|
|`--id` omitted.|`2`|

## See also

- [`proposal seed`](proposal-seed.md) — **canonical form; prefer this.**
- [Plans group overview](README.md) — proposal/plan naming cutover.
