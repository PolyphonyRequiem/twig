---
command: workspace area add
group: workspace
summary: Add an area path to workspace configuration.
stability: stable
mutates: local
---

# `twig workspace area add`

Add an area path to the workspace's area filter. By default the path matches
its entire subtree (`under` semantics); `--exact` restricts the match to the
path itself.

## Synopsis

```
twig workspace area add <path> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<path>`|yes|Area path to add, e.g. `Contoso\Team A`. Must be a valid area path — the string is parsed by `AreaPath.Parse`, and invalid input fails fast.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--exact`|`bool`|`false`|Use exact-match semantics. Without this flag the entry stores `IncludeChildren = true` so the filter matches the whole subtree.|
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `AreaCommand.AddAsync`
(`src/Twig/Commands/AreaCommand.cs:174-209`):

1. Parse via `AreaPath.Parse(path)`. On failure, print
   `Invalid area path: <error>` to stderr and exit `2`
   (`src/Twig/Commands/AreaCommand.cs:178-183`).
2. Load-or-init `config.Defaults.AreaPathEntries`.
3. Case-insensitive duplicate check on `.Path`. Duplicates fail with
   `Area path '<path>' is already configured.` on stderr and exit `1`
   (`src/Twig/Commands/AreaCommand.cs:187-195`).
4. Append the entry with `IncludeChildren = !exact` and persist via
   `config.SaveSplitAsync(paths, ct)` — this writes the split config
   (`twig.json` + `.twig/config`) documented in AB#3296.
5. Emit an `areaPathAdded` record whose message includes the semantics label
   (`under` or `exact`) drawn from `AreaPathEntry.SemanticsLabel`.

No ADO calls are made — the command mutates workspace configuration only.

## Examples

```
$ twig workspace area add "Contoso\Team A"
Added area path 'Contoso\Team A' (under).
```

```
$ twig workspace area add "Contoso\Team A" --exact -o json
{"kind":"areaPathAdded","path":"Contoso\\Team A","includeChildren":false,"message":"Added area path 'Contoso\\Team A' (exact)."}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|Invalid area path syntax|`2` with parser error on stderr|
|Duplicate entry (case-insensitive)|`1` with error on stderr|

## See also

- [`workspace area remove`](./area-remove.md)
- [`workspace area list`](./area-list.md)
- [`workspace area sync`](./area-sync.md)
- [`workspace area`](./area.md)
