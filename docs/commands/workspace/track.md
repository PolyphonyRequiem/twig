---
command: workspace track
group: workspace
summary: Track a single work item by ID (pinned to workspace).
stability: stable
mutates: local
---

# `twig workspace track`

Pin a single work item to the current Bench so it shows up in the workspace
view even when it is not in a subscribed sprint or area. Use this to keep a
one-off cross-team item in sight, or to babysit an item that would otherwise
drop off the workspace once its iteration rolls.

## Synopsis

```
twig workspace track <id> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<id>`|yes|Work item ID to track. Must be a positive integer; seed IDs (negative) are rejected.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `TrackingCommand.TrackAsync`
(`src/Twig/Commands/TrackingCommand.cs:35-36`), which invokes
`PinWorkflow.PinAsync(id, includeSubtree: false, ct)` on the current Bench
(`src/Twig/Commands/TrackingCommand.cs:170-201`). The workflow adds an item
selector to the Bench; the work item's title is looked up from the local cache
for display but is not required for the pin to land.

Non-positive IDs (`id <= 0`, including seed IDs) are rejected with
`Cannot track seeds or invalid IDs. Provide a positive work item ID.` on stderr
and exit code `2` (`src/Twig/Commands/TrackingCommand.cs:174-178`).

The pin is a Bench selector, not a snapshot: it does not pre-expand any
descendants, and the item continues to be resolved live at workspace-evaluation
time.

## Examples

```
$ twig workspace track 4211
Tracking #4211: Wire retry telemetry
```

```
$ twig workspace track 4211 -o json
{"kind":"tracked","id":4211,"title":"Wire retry telemetry","mode":"single","message":"Tracking #4211: Wire retry telemetry"}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|`id <= 0` (seed or invalid ID)|`2` with error on stderr|

## See also

- [`workspace track-tree`](./track-tree.md)
- [`workspace untrack`](./untrack.md)
- [`workspace exclude`](./exclude.md)
- [`workspace`](./workspace.md)
