---
command: plan preview
group: plans
summary: Deprecated alias for `proposal preview`, including its optional native presentation.
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
twig plan preview --file <path> [--full] [--interactive] [--include-rendering --width <columns> --color <always|never>] [-o human|json|minimal]
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
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`; native rendering requires `json`.|
|`--full`|flag|`false`|Expand description bodies from the captured review. JSON is always exact.|
|`--interactive`|flag|`false`|Opt into a human-terminal Details/Back/Cancel review loop. Never authorizes or applies.|
|`--include-rendering`|flag|`false`|Pass through the canonical proposal-preview presentation envelope.|
|`--width`|integer|`unbounded / 120`|Presentation width; omitted human output is unbounded, while an included JSON frame defaults to 120. Explicit values are inclusive 20..400.|
|`--color`|string|`never`|Presentation color mode: `always` or `never`; OMP requests `always` unless `NO_COLOR` requires `never`.|

## Behavior

Alias only. See [`proposal preview`](proposal-preview.md#behavior) and its [native OMP presentation](proposal-preview.md#native-presentation-for-omp) section for the full behavior contract. The alias preserves the canonical command's default plain/human and JSON output; optional rendering is still opt-in, and all retained-observation, digest and no-approval rules are identical.

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
|`--interactive` with non-human output or redirected input/output.|`2` (before preview or journal import)|
|Invalid rendering options — non-JSON output with `--include-rendering`, width outside 20..400, or unsupported color value.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — **canonical form; prefer this.**
- [Plans group overview](README.md) — proposal/plan naming cutover.
- [Twig presentation guidance](../../../.github/skills/twig/references/presentation.md) — retained-observation rules for rich hosts.
