---
command: link parent
group: links
summary: Set the parent of the active (or targeted) work item.
stability: stable
mutates: ado
---

# `twig link parent`

Attach a hierarchy parent to a published work item. Reach for it when the item
has no parent yet — the command refuses to overwrite an existing parent, and
directs you at `link reparent` in that case.

## Synopsis

```
twig link parent <targetId> [childId] [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`targetId`|yes|Work item ID to set as the parent.|
|`childId`|no|Work item ID to parent. Optional second positional; defaults to the active item.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Alternative to the second positional: name the child by option instead. `childId` takes precedence when both are provided.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

The child is either the active work item or the item named by the second
positional argument / `--id`. Mutation flow: guard checks (self-link,
already-parented, target exists) → `System.LinkTypes.Hierarchy-Reverse` link
added on the child → both endpoints resynced (`src/Twig/Commands/LinkCommand.cs:60`).

Failing guards:
- Parenting an item to itself is rejected (`src/Twig/Commands/LinkCommand.cs:444`).
- If the child already has any parent, the command refuses and points at
  `twig link reparent` (`src/Twig/Commands/LinkCommand.cs:76`).
- If the child is already parented to `targetId`, the command emits
  `linkUnchanged` and exits `0` (`src/Twig/Commands/LinkCommand.cs:449`).
- If `targetId` cannot be resolved, the command fails without mutating ADO
  (`src/Twig/Commands/LinkCommand.cs:84`).

## Examples

Parent the active item under `5678`:

```
$ twig link parent 5678
#1234 is now a child of #5678.
  #1234 ──Parent──▶ #5678
```

Parent an arbitrary child without changing active context, in JSON:

```
$ twig link parent 5678 1234 -o json
{
  "kind": "linkParented",
  "message": "#1234 is now a child of #5678.",
  "count": 1,
  "links": [ { "sourceId": 1234, "targetId": 5678, "linkType": "Parent" } ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Parent added or already correct|`0`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|Child already has a different parent|`1`|
|Target work item not found in ADO|`1`|

## See also

- [`link reparent`](./link-reparent.md)
- [`link unparent`](./link-unparent.md)
- [`link artifact`](./link-artifact.md)
- [Group overview](./README.md)
