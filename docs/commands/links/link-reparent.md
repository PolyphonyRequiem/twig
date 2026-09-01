---
command: link reparent
group: links
summary: Remove the current parent and set a new one in a single operation.
stability: stable
mutates: ado
---

# `twig link reparent`

Move a work item under a new parent. Works whether or not the item already has
a parent — the previous parent (if any) is removed before the new one is added.
Prefer this over `link unparent` + `link parent` when you already know the
destination: one command, one journal path, and both operations share the
same guard set.

## Synopsis

```
twig link reparent <targetId> [childId] [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`targetId`|yes|Work item ID to set as the new parent.|
|`childId`|no|Work item ID to move. Optional second positional; defaults to the active item.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Alternative to the second positional: name the child by option instead. `childId` takes precedence when both are provided.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Resolves the child, runs the shared parenting guards (self-link, no-op when
already parented to the target), verifies the new target exists, then removes
the old `System.LinkTypes.Hierarchy-Reverse` edge (if any) and adds the new
one (`src/Twig/Commands/LinkCommand.cs:166`). Three items are resynced when
an old parent is being replaced: the child, the new parent, and the old
parent (`src/Twig/Commands/LinkCommand.cs:202`). The two ADO writes are not
transactional — if the add fails after the remove, the child is left
parentless. Rerun the command to recover.

If the child is already parented to `targetId`, the command emits
`linkUnchanged` and exits `0` without touching ADO
(`src/Twig/Commands/LinkCommand.cs:449`).

## Examples

Move the active item under a new parent:

```
$ twig link reparent 5678
#1234 reparented from #4200 to #5678.
  #1234 ──Parent──▶ #5678
```

Move a specific child, JSON output:

```
$ twig link reparent 5678 1234 -o json
{
  "kind": "linkReparented",
  "message": "#1234 reparented from #4200 to #5678.",
  "count": 1,
  "links": [ { "sourceId": 1234, "targetId": 5678, "linkType": "Parent" } ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Reparented successfully, or already parented to `targetId`|`0`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|New parent target not found in ADO|`1`|

## See also

- [`link parent`](./link-parent.md)
- [`link unparent`](./link-unparent.md)
- [Group overview](./README.md)
