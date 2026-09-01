---
command: area remove
group: workspace
summary: Deprecated alias for `workspace area remove`. Prints a deprecation hint on stderr.
stability: stable
mutates: local
---

# `twig area remove`

**Deprecated.** Use [`twig workspace area remove`](./workspace-area-remove.md)
instead. The alias is registered as `[Hidden] [Command("area remove")]` and
delegates after emitting a hint (`src/Twig/Program.cs:1236-1242`).

## Synopsis

```
twig area remove <path> [flags]
```

## Arguments

|Argument|Required|Description|
|`<path>`|yes|Area path to remove.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

1. Emit `hint: 'twig area remove' is deprecated. Use 'twig workspace area remove' instead.`
   on stderr (`src/Twig/Program.cs:1240`).
2. Delegate to `AreaCommand.RemoveAsync(path, output, ct)`
   (`src/Twig/Program.cs:1241`).

All behavior, validation, exit codes, and output shapes match
[`workspace area remove`](./workspace-area-remove.md).

## Examples

```
$ twig area remove "Contoso\Team A"
hint: 'twig area remove' is deprecated. Use 'twig workspace area remove' instead.
Removed area path 'Contoso\Team A'.
```

```
$ twig area remove "Contoso\Team A" -o json
hint: 'twig area remove' is deprecated. Use 'twig workspace area remove' instead.
{"kind":"areaPathRemoved","path":"Contoso\\Team A","message":"Removed area path 'Contoso\\Team A'."}
```

## Exit codes and failure modes

Identical to [`workspace area remove`](./workspace-area-remove.md).

|Condition|Result|
|Success|`0`|
|No area paths configured|`1`|
|Path not found|`1`|

## See also

- [`workspace area remove`](./workspace-area-remove.md) — canonical form
- [`area`](./area-deprecated.md)
- [`area add`](./area-add-deprecated.md)
