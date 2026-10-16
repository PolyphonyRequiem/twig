---
command: workspace untrack
group: workspace
summary: Remove a work item from tracking.
stability: stable
mutates: local
---

# `twig workspace untrack`

Remove explicit pin selectors rooted at a positive work item ID from the current
Bench. Omitting `--mode` removes both the single-item and subtree pins. Use
`--mode single` or `--mode tree` to remove only that kind and retain the other.
An item can remain visible through another pin, an ancestor subtree, a query,
or protected seed/pending membership.

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
|`--expect-bench`|`string`|unset|Refuse a changed current Bench instead of retargeting.|
|`--expect-binding`|`string`|unset|Refuse a changed native connection binding; never selects one.|
|`--expect-identity`|`string`|unset|Refuse a changed authenticated principal; never selects one.|
|`--expect-settings`|`string`|unset|Compare the captured raw query-settings digest inside the atomic pin transaction.|
|`--mode`|`string`|unset|Remove only `single` or `tree` (case-insensitive); unset removes both explicit kinds.|

## Behavior

Removal routes through `PinWorkflow.UnpinAsync` and the native
`IBenchRepository.TryRemovePinsAsync` transaction. The captured current Bench
and optional raw query-settings digest are checked in the same transaction as
the deletion. Connection binding and identity guards remain supported. A stale
guard refuses the operation without retargeting or changing pins.

Only selectors of the requested kind with that exact ID are deleted. The other
kind on the same ID, ancestor pins, queries, other Benches, seeds and pending
work are unchanged. Repeating a removal succeeds as a no-op; it never toggles
a pin back on or removes and re-adds the other kind.

The default command retains its existing confirmation and JSON fields:
`message` and `itemId`. It reports `Untracked #<id>.` when the transaction
actually removed a pin, or `#<id> was not tracked.` otherwise.

A typed removal additionally reports `pinMode: single|tree` and `wasPinned`
in JSON. `wasPinned` is true only if this transaction deleted the named kind,
not merely because the earlier displayed state contained it. Human/minimal
output names the kind: `Removed single pin for #<id>.` or
`#<id> has no explicit single pin.` (subtree for `--mode tree`). Both changed
and no-op results exit `0` and ensure the requested explicit kind is absent.

Non-positive IDs and any explicit mode other than `single` or `tree` are usage
errors (`2`) with format-aware errors on stderr, before any mutation.

## Examples

```
$ twig workspace untrack 4211
Untracked #4211.
```

```
$ twig workspace untrack 4211 -o json
{"message":"Untracked #4211.","itemId":4211}
```

```
$ twig workspace untrack 4211 --mode single --expect-bench 7 -o json
{"message":"Removed single pin for #4211.","itemId":4211,"pinMode":"single","wasPinned":true}
```

```
$ twig workspace untrack 4211 --mode tree -o json
{"message":"#4211 has no explicit subtree pin.","itemId":4211,"pinMode":"tree","wasPinned":false}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success (item was tracked or was not tracked)|`0`|
|`id <= 0` (seed or invalid ID)|`2` with error on stderr|
|Invalid explicit `--mode` (including empty/numeric/`both`)|`2` with error on stderr; no mutation|
|Stale captured Bench, settings, binding or identity|`1` with error on stderr; no mutation|

## See also

- [`workspace track`](./track.md)
- [`workspace track-tree`](./track-tree.md)
- [`workspace exclude`](./exclude.md)
- [`workspace`](./workspace.md)
