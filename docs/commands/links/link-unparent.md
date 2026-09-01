---
command: link unparent
group: links
summary: Remove the parent link from the active (or targeted) work item.
stability: stable
mutates: ado
---

# `twig link unparent`

Detach a hierarchy parent from a published work item without picking a new one.
Reach for it when the item genuinely belongs at the top level, or when you
want to stage the child before deciding on a new parent. If you already know
the new parent, prefer `link reparent` — it does both in one hop.

## Synopsis

```
twig link unparent [childId] [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`childId`|no|Work item ID to unparent. Optional positional; defaults to the active item.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Alternative to the positional: name the item by option instead. `childId` takes precedence when both are provided.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Resolves the item, verifies it has a parent, removes the
`System.LinkTypes.Hierarchy-Reverse` edge, and resyncs both endpoints
(`src/Twig/Commands/LinkCommand.cs:118`). If the item has no parent, the
command fails rather than reporting a no-op — you asked to remove something
that was not there (`src/Twig/Commands/LinkCommand.cs:131`).

## Examples

Unparent the active item:

```
$ twig link unparent
Removed parent #5678 from #1234.
```

Unparent an arbitrary item, minimal output:

```
$ twig link unparent 1234 -o minimal
Removed parent #5678 from #1234.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Parent removed|`0`|
|Active/target item not found in cache|`1`|
|Item has no parent link to remove|`1`|

## See also

- [`link parent`](./link-parent.md)
- [`link reparent`](./link-reparent.md)
- [Group overview](./README.md)
