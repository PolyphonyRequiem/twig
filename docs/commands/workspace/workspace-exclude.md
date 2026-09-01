---
command: workspace exclude
group: workspace
summary: Exclude a work item from workspace view.
stability: stable
mutates: local
---

# `twig workspace exclude`

Hide a specific work item from the workspace view without touching its state
in ADO. Reach for `exclude` when a noisy item — a long-lived spike, a
placeholder, an item you keep re-seeing but never plan to work — is cluttering
your sprint or area rendering.

## Synopsis

```
twig workspace exclude <id> [flags]
```

## Arguments

|Argument|Required|Description|
|`<id>`|yes|Work item ID to exclude. Positive integer only.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `TrackingCommand.ExcludeAsync`
(`src/Twig/Commands/TrackingCommand.cs:67-80`), which calls
`ITrackingService.ExcludeAsync(id, ct)` and emits an `excluded` outcome record.
The exclusion is persisted in the workspace's local tracking store; ADO is not
touched.

Non-positive IDs are rejected with
`Cannot exclude seeds or invalid IDs. Provide a positive work item ID.` on
stderr and exit code `2` (`src/Twig/Commands/TrackingCommand.cs:71-75`).

Exclusion is idempotent from the caller's perspective — re-excluding an already
excluded ID succeeds and re-emits the same outcome.

## Examples

```
$ twig workspace exclude 3980
Excluded #3980 from workspace view.
```

```
$ twig workspace exclude 3980 -o json
{"kind":"excluded","id":3980,"message":"Excluded #3980 from workspace view."}
```

## Exit codes and failure modes

|Condition|Result|
|Success|`0`|
|`id <= 0` (seed or invalid ID)|`2` with error on stderr|

## See also

- [`workspace exclusions`](./workspace-exclusions.md)
- [`workspace untrack`](./workspace-untrack.md)
- [`workspace`](./workspace.md)
