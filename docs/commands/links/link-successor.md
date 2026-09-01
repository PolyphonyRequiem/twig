---
command: link successor
group: links
summary: Mark the active (or targeted) item as blocking another item.
stability: stable
mutates: ado
---

# `twig link successor`

Record a `Successor` edge — this item must complete before the named target
can proceed. The mirror of [`link predecessor`](./link-predecessor.md); pick
whichever side of the dependency you happen to be on, ADO materialises the
reverse edge itself.

## Synopsis

```
twig link successor <targetId> [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`targetId`|yes|Work item ID that this item blocks.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Target a specific work item by ID instead of the active item.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Routes through the shared dependency core
(`src/Twig/Commands/LinkCommand.cs:242`) with `linkType = Successor`.
Self-links are rejected, the target must exist in ADO, and if the edge is
already present the command emits `linkUnchanged` and exits `0`
(`src/Twig/Commands/LinkCommand.cs:374`). Both endpoints are resynced on
success — the reverse `Predecessor` edge lands on `targetId`, so a cache that
only knows about the named side would be stale
(`src/Twig/Commands/LinkCommand.cs:394`).

Cycle detection is deliberately not implemented; see the note on
[`link predecessor`](./link-predecessor.md).

## Examples

Mark the active item as blocking `#66`:

```
$ twig link successor 66
#1234 now has successor #66.
  #1234 ──successor──▶ #66
```

Mark a specific item as blocking, JSON output:

```
$ twig link successor 66 --id 65 -o json
{
  "kind": "linkAdded",
  "message": "#65 now has successor #66.",
  "count": 1,
  "links": [ { "sourceId": 65, "targetId": 66, "linkType": "successor" } ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Successor added, or already present|`0`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|Target work item not found in ADO|`1`|
|ADO rejected the write|`1`|

## See also

- [`link predecessor`](./link-predecessor.md)
- [`link unlink`](./link-unlink.md)
- [Group overview](./README.md)
