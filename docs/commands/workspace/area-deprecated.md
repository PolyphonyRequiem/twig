---
command: area
group: workspace
summary: Deprecated alias for `workspace area`. Prints a deprecation hint on stderr.
stability: stable
mutates: none
---

# `twig area`

**Deprecated.** Use [`twig workspace area`](./area.md) instead. This
top-level `area` verb is retained as a hidden alias so that existing scripts
and skills keep working while callers migrate. It writes a `hint:` line on
stderr and then delegates to the canonical implementation
(`src/Twig/Program.cs:1213-1219`).

## Synopsis

```
twig area [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Registered as `[Hidden] [Command("area")]` in `Program.cs`. On invocation the
command:

1. Writes `hint: 'twig area' is deprecated. Use 'twig workspace area' instead.`
   to stderr (`src/Twig/Program.cs:1217`).
2. Delegates to `AreaCommand.ViewAsync(output, ct)` — the same code path used
   by `twig workspace area` (`src/Twig/Program.cs:1218`).

All other behavior, exit codes, and output shapes match the canonical form.
See [`workspace area`](./area.md) for details.

## Examples

```
$ twig area
hint: 'twig area' is deprecated. Use 'twig workspace area' instead.
Area \Contoso\Team A (under):
  #4200  Retry policy epic
  …
```

```
$ twig area -o json
hint: 'twig area' is deprecated. Use 'twig workspace area' instead.
{"areaView":{"filters":[…],"matchCount":3,"items":[…]}}
```

## Exit codes and failure modes

Identical to [`workspace area`](./area.md).

|Condition|Result|
| --- | --- |
|Success|`0`|
|Local cache unavailable|`1`|

## See also

- [`workspace area`](./area.md) — canonical form
- [`area add`](./area-add-deprecated.md)
- [`area list`](./area-list-deprecated.md)
