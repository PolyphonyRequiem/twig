---
command: workspace area remove
group: workspace
summary: Remove an area path from workspace configuration.
stability: stable
mutates: local
---

# `twig workspace area remove`

Remove a previously-configured area path. The lookup is case-insensitive and
matches on the stored `Path` string; `--exact` versus `under` semantics are
irrelevant to the removal.

## Synopsis

```
twig workspace area remove <path> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<path>`|yes|Area path to remove. Matched case-insensitively against the stored `Path` value.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `AreaCommand.RemoveAsync`
(`src/Twig/Commands/AreaCommand.cs:212-236`):

1. If no area paths are configured (`AreaPathEntries` is null or empty), print
   `No area paths configured.` to stderr and exit `1`
   (`src/Twig/Commands/AreaCommand.cs:216-220`).
2. Case-insensitive `FindIndex` on `AreaPathEntries[].Path`. Not found fails
   with `Area path '<path>' is not configured.` on stderr and exit `1`
   (`src/Twig/Commands/AreaCommand.cs:222-229`).
3. Remove the entry at the found index and persist via
   `config.SaveSplitAsync(paths, ct)`.
4. Emit an `areaPathRemoved` record with the removed path echoed back.

No ADO calls are made.

## Examples

```
$ twig workspace area remove "Contoso\Team A"
Removed area path 'Contoso\Team A'.
```

```
$ twig workspace area remove "Contoso\Team A" -o json
{"kind":"areaPathRemoved","path":"Contoso\\Team A","message":"Removed area path 'Contoso\\Team A'."}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|No area paths configured|`1` with `No area paths configured.` on stderr|
|Path not found (case-insensitive)|`1` with error on stderr|

## See also

- [`workspace area add`](./area-add.md)
- [`workspace area list`](./area-list.md)
- [`workspace area sync`](./area-sync.md)
