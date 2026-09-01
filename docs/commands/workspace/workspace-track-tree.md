---
command: workspace track-tree
group: workspace
summary: Track a work item and its subtree.
stability: stable
mutates: local
---

# `twig workspace track-tree`

Pin a work item together with its entire descendant subtree. Reach for it when
you own an epic or feature and want every child that already exists — and
every one that gets created later — to appear in the workspace view without
adding pins one by one.

## Synopsis

```
twig workspace track-tree <id> [flags]
```

## Arguments

|Argument|Required|Description|
|`<id>`|yes|Work item ID to track along with its descendants. Positive integer only.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `TrackingCommand.TrackTreeAsync`
(`src/Twig/Commands/TrackingCommand.cs:39-40`), which calls
`PinWorkflow.PinAsync(id, includeSubtree: true, ct)`
(`src/Twig/Commands/TrackingCommand.cs:170-201`). The workflow adds a *subtree
selector* to the current Bench. The subtree is **not** expanded at pin time:
it is matched live at every workspace evaluation, which is what makes newly
created children appear automatically
(`src/Twig/Commands/TrackingCommand.cs:180-183`).

Non-positive IDs are rejected the same way as `workspace track`: exit code
`2` with `Cannot track seeds or invalid IDs. Provide a positive work item ID.`
on stderr.

The human-format output appends ` (tree)` to the confirmation message so it is
distinct from a single-item pin
(`src/Twig/Commands/TrackingCommand.cs:186-198`).

## Examples

```
$ twig workspace track-tree 4200
Tracking #4200: Retry policy epic (tree)
```

```
$ twig workspace track-tree 4200 -o json
{"kind":"tracked","id":4200,"title":"Retry policy epic","mode":"tree","message":"Tracking #4200: Retry policy epic (tree)"}
```

## Exit codes and failure modes

|Condition|Result|
|Success|`0`|
|`id <= 0` (seed or invalid ID)|`2` with error on stderr|

## See also

- [`workspace track`](./workspace-track.md)
- [`workspace untrack`](./workspace-untrack.md)
- [`workspace`](./workspace.md)
