---
command: workspace untrack
group: workspace
summary: Remove a work item from tracking.
stability: stable
mutates: local
---

# `twig workspace untrack`

Remove a previously-pinned work item (or subtree selector rooted at the given
ID) from the current Bench. Use it when you no longer need an item in the
workspace view — for example after handing off a cross-team item, or when a
tracked epic is closed.

## Synopsis

```
twig workspace untrack <id> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<id>`|yes|Work item ID to stop tracking. Positive integer only.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `TrackingCommand.UntrackAsync`
(`src/Twig/Commands/TrackingCommand.cs:43-64`). Removal routes through
`PinWorkflow.UnpinAsync(id, ct)` (ADO #145) so both the CLI and MCP surfaces
share a single mutation seam.

The command succeeds either way: if the ID was pinned it reports `Untracked
#<id>.` with the `untracked` outcome kind; if the ID was not pinned it reports
`#<id> was not tracked.` with the `untrackNotTracked` outcome kind and severity
`info` (`src/Twig/Commands/TrackingCommand.cs:56-62`). In both cases the exit
code is `0`.

Non-positive IDs are rejected with
`Cannot untrack seeds or invalid IDs. Provide a positive work item ID.` on
stderr and exit code `2` (`src/Twig/Commands/TrackingCommand.cs:47-51`).

## Examples

```
$ twig workspace untrack 4211
Untracked #4211.
```

```
$ twig workspace untrack 4211 -o json
{"kind":"untracked","id":4211,"message":"Untracked #4211."}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success (item was tracked or was not tracked)|`0`|
|`id <= 0` (seed or invalid ID)|`2` with error on stderr|

## See also

- [`workspace track`](./track.md)
- [`workspace track-tree`](./track-tree.md)
- [`workspace exclude`](./exclude.md)
- [`workspace`](./workspace.md)
