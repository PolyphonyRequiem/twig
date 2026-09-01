---
command: workspace exclusions
group: workspace
summary: List all excluded work items; also clears or removes exclusions.
stability: stable
mutates: local
---

# `twig workspace exclusions`

Inspect and manage the workspace's exclusion list — the IDs that
`workspace exclude` has hidden from the view. With no flags it prints the
current exclusions. `--clear` removes them all; `--remove <id>` removes one.

## Synopsis

```
twig workspace exclusions [flags]
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
|`--clear`|`bool`|`false`|Remove all exclusions.|
|`--remove`|`int?`|`null`|Remove a specific exclusion by work item ID.|

## Behavior

Delegates to `TrackingCommand.ExclusionsAsync`
(`src/Twig/Commands/TrackingCommand.cs:83-168`). The three sub-modes are
resolved in order:

1. `--clear` calls `trackingService.ClearExclusionsAsync(ct)`. On non-empty
   input it reports the removed count via the `exclusionsCleared` outcome; on
   an already-empty list it emits `exclusionsClearedEmpty` at severity `info`
   (`src/Twig/Commands/TrackingCommand.cs:87-95`).
2. `--remove <id>` calls `trackingService.RemoveExclusionAsync(removeId, ct)`.
   A successful removal emits `exclusionRemoved`; an ID that was not excluded
   emits `exclusionRemoveNotFound` at severity `info`
   (`src/Twig/Commands/TrackingCommand.cs:97-111`). Non-positive IDs are
   rejected with exit `2` and `Provide a positive work item ID to remove.`
   on stderr.
3. Otherwise the command lists the exclusions. On machine formats
   (`json`, `json-full`, `json-compact`, `ids`) it emits an `exclusionsList`
   Document with a table of `id`/`title` rows and a `count` field
   (`src/Twig/Commands/TrackingCommand.cs:114-145`). On human format it
   streams `#<id>: <title>` lines and a total-count footer
   (`src/Twig/Commands/TrackingCommand.cs:156-166`).

Titles are looked up from the local cache; if the cache does not know the
title, the row shows just `#<id>` without failing.

## Examples

```
$ twig workspace exclusions
#3980: Auth loop repro
#4102: Retry spike
2 exclusion(s) total.
```

```
$ twig workspace exclusions --remove 3980 -o json
{"kind":"exclusionRemoved","id":3980,"message":"Removed exclusion for #3980."}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|`--remove <id>` with `id <= 0`|`2` with error on stderr|
|`--clear` on an empty list|`0` with `exclusionsClearedEmpty` outcome|

## See also

- [`workspace exclude`](./exclude.md)
- [`workspace untrack`](./untrack.md)
- [`workspace`](./workspace.md)
