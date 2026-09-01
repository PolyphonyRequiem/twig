---
command: workspace area
group: workspace
summary: Show the area-filtered workspace view.
stability: stable
mutates: none
---

# `twig workspace area`

Render the workspace filtered by the configured area paths. Reach for it when
you want to see everything under your team's area(s) — not just the sprint
slice — with the same hierarchy the workspace view uses. The command reads
from the local cache; run `twig sync` or `twig workspace --refresh` first if
you need fresh data.

## Synopsis

```
twig workspace area [flags]
```

## Arguments

|Argument|Required|Description|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `AreaCommand.ViewAsync`
(`src/Twig/Commands/AreaCommand.cs:35-127`). The pipeline is:

1. Resolve configured area paths from `config.Defaults.ResolveAreaPaths()`.
   When no area paths are configured, emit the `noAreaPathsConfigured` info
   message and exit `0` (`src/Twig/Commands/AreaCommand.cs:42-47`).
2. Query the local cache with `IWorkItemRepository.GetByAreaPathsAsync` using
   the configured `IncludeChildren` semantics of each entry
   (`src/Twig/Commands/AreaCommand.cs:55-62`).
3. For hits, hydrate parent chains so the hierarchy renders even when parents
   are outside the area filter (`src/Twig/Commands/AreaCommand.cs:74-93`).
4. Build a `SprintHierarchy` with `IsSprintItem = IsInArea`, so the tree
   marks each matched item without collapsing its parent context
   (`src/Twig/Commands/AreaCommand.cs:96-114`).
5. Render through the `AreaView` renderer for machine formats, or the
   legacy Spectre area-view tree for human format
   (`src/Twig/Commands/AreaCommand.cs:117-126`).

If the local cache is unavailable (no `IWorkItemRepository` wired) the command
fails fast with `Cannot show area view: no local cache available.` on stderr
and exit `1` (`src/Twig/Commands/AreaCommand.cs:49-53`).

## Examples

```
$ twig workspace area
Area \Contoso\Team A (under):
  #4200  Retry policy epic
    #4211  Wire retry telemetry            Doing
    #4212  Retry policy unit tests         To do
1 area path, 3 matches.
```

```
$ twig workspace area -o json
{"areaView":{"filters":[{"path":"\\Contoso\\Team A","includeChildren":true}],"matchCount":3,"items":[…]}}
```

## Exit codes and failure modes

|Condition|Result|
|Success (matches found or none)|`0`|
|Local cache unavailable|`1` with `Cannot show area view: no local cache available.` on stderr|

## See also

- [`workspace area add`](./workspace-area-add.md)
- [`workspace area list`](./workspace-area-list.md)
- [`workspace area sync`](./workspace-area-sync.md)
- [`workspace`](./workspace.md)
