---
command: area add
group: workspace
summary: Deprecated alias for `workspace area add`. Prints a deprecation hint on stderr.
stability: stable
mutates: local
---

# `twig area add`

**Deprecated.** Use [`twig workspace area add`](./area-add.md)
instead. This alias is registered as `[Hidden] [Command("area add")]` and
delegates to the canonical implementation after emitting a hint
(`src/Twig/Program.cs:1225-1231`).

## Synopsis

```
twig area add <path> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<path>`|yes|Area path to add, e.g. `Contoso\Team A`.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--exact`|`bool`|`false`|Use exact-match semantics instead of subtree (`under`).|
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

1. Emit `hint: 'twig area add' is deprecated. Use 'twig workspace area add' instead.`
   on stderr (`src/Twig/Program.cs:1229`).
2. Delegate to `AreaCommand.AddAsync(path, exact, output, ct)`
   (`src/Twig/Program.cs:1230`).

All behavior, validation, exit codes, and output shapes match
[`workspace area add`](./area-add.md).

## Examples

```
$ twig area add "Contoso\Team A"
hint: 'twig area add' is deprecated. Use 'twig workspace area add' instead.
Added area path 'Contoso\Team A' (under).
```

```
$ twig area add "Contoso\Team A" --exact -o json
hint: 'twig area add' is deprecated. Use 'twig workspace area add' instead.
{"kind":"areaPathAdded","path":"Contoso\\Team A","includeChildren":false,"message":"Added area path 'Contoso\\Team A' (exact)."}
```

## Exit codes and failure modes

Identical to [`workspace area add`](./area-add.md).

|Condition|Result|
| --- | --- |
|Success|`0`|
|Invalid area path syntax|`2`|
|Duplicate entry|`1`|

## See also

- [`workspace area add`](./area-add.md) — canonical form
- [`area`](./area-deprecated.md)
- [`area remove`](./area-remove-deprecated.md)
